using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mycroft.Core.Enums;
using Mycroft.Core.Interfaces;
using Mycroft.Infrastructure;
using Mycroft.Infrastructure.Persistence;
using Mycroft.Infrastructure.Processing;

namespace Mycroft.Api.Workers;

/// <summary>
/// Consumes job ids from the in-memory channel. On startup it re-enqueues every Pending/Processing job
/// found in the database (crash recovery), so nothing accepted with 202 is ever lost.
/// </summary>
public sealed class ReconciliationWorker(
    IServiceScopeFactory scopeFactory,
    IReconciliationJobQueue queue,
    IOptions<MycroftOptions> options,
    ILogger<ReconciliationWorker> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);

        var consumers = Enumerable.Range(0, Math.Max(1, options.Value.WorkerConcurrency))
            .Select(_ => ConsumeAsync(stoppingToken));
        await Task.WhenAll(consumers);
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MycroftDbContext>();
            var ids = await db.ReconciliationJobs
                .Where(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Processing)
                .OrderBy(j => j.CreatedAt)
                .Select(j => j.Id)
                .ToListAsync(ct);

            foreach (var id in ids) await queue.EnqueueAsync(id, ct);
            if (ids.Count > 0) logger.LogInformation("Recovered {Count} unfinished jobs after startup", ids.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Crash recovery failed; unfinished jobs will be picked up on next restart");
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var jobId in queue.DequeueAllAsync(ct))
            {
                if (!_inFlight.TryAdd(jobId, 0)) continue; // duplicate signal for a job already running
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var processor = scope.ServiceProvider.GetRequiredService<ReconciliationProcessor>();
                    await processor.ProcessAsync(jobId, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unhandled error while processing job {JobId}", jobId);
                }
                finally
                {
                    _inFlight.TryRemove(jobId, out _);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
