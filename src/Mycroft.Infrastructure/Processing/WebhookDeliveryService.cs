using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mycroft.Core.Enums;
using Mycroft.Core.Interfaces;
using Mycroft.Core.Models;
using Mycroft.Infrastructure.Persistence;

namespace Mycroft.Infrastructure.Processing;

/// <summary>
/// Delivers due webhooks. First attempt is immediate on completion, then retries follow the
/// backoff schedule (15s, 1m, 5m, 15m, 1h). After the last retry the webhook is marked Failed;
/// results remain available through GET.
/// </summary>
public sealed class WebhookDeliveryService(
    MycroftDbContext db,
    IWebhookDispatcher dispatcher,
    IOptions<MycroftOptions> options,
    ILogger<WebhookDeliveryService> logger,
    TimeProvider? timeProvider = null)
{
    public const string CompletedEvent = "reconciliation.completed";
    public const string FailedEvent = "reconciliation.failed";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <returns>Number of deliveries attempted.</returns>
    public async Task<int> DeliverDueAsync(int batchSize, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var due = await db.ReconciliationJobs
            .Where(j => j.WebhookStatus == WebhookStatus.Pending && j.NextWebhookRetryAt <= now)
            .OrderBy(j => j.NextWebhookRetryAt)
            .Take(batchSize)
            .ToListAsync(ct);

        foreach (var job in due)
        {
            var items = await db.TransactionItems.AsNoTracking().Where(i => i.JobId == job.Id).ToListAsync(ct);
            var eventName = job.Status == JobStatus.Failed ? FailedEvent : CompletedEvent;
            var body = JsonSerializer.Serialize(JobResultView.From(job, items, eventName), MycroftJson.Options);

            var result = await dispatcher.DispatchAsync(job.CallbackUrl!, job.WebhookSecret, eventName, body, ct);
            job.WebhookAttempts++;

            if (result.Success)
            {
                job.WebhookStatus = WebhookStatus.Delivered;
                job.NextWebhookRetryAt = null;
            }
            else
            {
                var schedule = options.Value.WebhookRetrySchedule;
                var retryIndex = job.WebhookAttempts - 1; // attempt 1 failed → first retry delay
                if (retryIndex < schedule.Count)
                {
                    job.NextWebhookRetryAt = _time.GetUtcNow() + schedule[retryIndex];
                }
                else
                {
                    job.WebhookStatus = WebhookStatus.Failed;
                    job.NextWebhookRetryAt = null;
                    logger.LogWarning("Webhook for job {JobId} failed permanently after {Attempts} attempts: {Error}", job.Id, job.WebhookAttempts, result.Error);
                }
            }

            await db.SaveChangesAsync(ct);
        }

        return due.Count;
    }
}
