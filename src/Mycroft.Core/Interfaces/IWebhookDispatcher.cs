namespace Mycroft.Core.Interfaces;

public interface IWebhookDispatcher
{
    Task<WebhookDeliveryResult> DispatchAsync(string callbackUrl, string? secret, string eventName, string jsonBody, CancellationToken cancellationToken = default);
}

public sealed record WebhookDeliveryResult(bool Success, int? StatusCode, string? Error);
