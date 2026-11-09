using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mycroft.Core.Enums;
using Mycroft.Infrastructure;
using Mycroft.Infrastructure.Persistence;

namespace Mycroft.Api.Workers;

/// <summary>
/// Deletes finished jobs (and, via FK cascade, their items) older than RETENTION_DAYS.
/// MerchantPatterns and Categories are kept forever.
/// </summary>
public sealed class RetentionCleanupWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<MycroftOptions> options,
    TimeProvider timeProvider,
    ILogger<RetentionCleanupWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<MycroftDbContext>();
                var cutoff = timeProvider.GetUtcNow().AddDays(-Math.Max(1, options.Value.RetentionDays));

                var deleted = await db.ReconciliationJobs
                    .Where(j => j.CreatedAt < cutoff
                                && j.Status != JobStatus.Pending && j.Status != JobStatus.Processing
                                && j.WebhookStatus != WebhookStatus.Pending)
                    .ExecuteDeleteAsync(stoppingToken);

                if (deleted > 0) logger.LogInformation("Retention: removed {Count} jobs older than {Cutoff:u}", deleted, cutoff);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Retention cleanup failed");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
        } while (true);
    }
}
