using Mycroft.Infrastructure.Processing;

namespace Mycroft.Api.Workers;

/// <summary>Polls for due webhooks (first attempts and exponential-backoff retries) and delivers them.</summary>
public sealed class WebhookRetryWorker(IServiceScopeFactory scopeFactory, ILogger<WebhookRetryWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private const int BatchSize = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                int delivered;
                do
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var service = scope.ServiceProvider.GetRequiredService<WebhookDeliveryService>();
                    delivered = await service.DeliverDueAsync(BatchSize, stoppingToken);
                } while (delivered == BatchSize && !stoppingToken.IsCancellationRequested);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Webhook delivery loop failed");
            }
        } while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
