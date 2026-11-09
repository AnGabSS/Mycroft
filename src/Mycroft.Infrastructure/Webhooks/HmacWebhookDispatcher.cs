using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mycroft.Core.Interfaces;

namespace Mycroft.Infrastructure.Webhooks;

/// <summary>
/// Delivers webhooks signed with HMAC-SHA256 over the exact request body:
/// <c>X-Mycroft-Signature: sha256=&lt;lowercase hex&gt;</c>.
/// </summary>
public sealed class HmacWebhookDispatcher(
    IHttpClientFactory httpClientFactory,
    IOptions<MycroftOptions> options,
    ILogger<HmacWebhookDispatcher> logger) : IWebhookDispatcher
{
    public const string HttpClientName = "mycroft-webhooks";
    public const string SignatureHeader = "X-Mycroft-Signature";
    public const string EventHeader = "X-Mycroft-Event";

    public async Task<WebhookDeliveryResult> DispatchAsync(string callbackUrl, string? secret, string eventName, string jsonBody, CancellationToken cancellationToken = default)
    {
        var effectiveSecret = string.IsNullOrEmpty(secret) ? options.Value.DefaultWebhookSecret : secret;

        using var request = new HttpRequestMessage(HttpMethod.Post, callbackUrl)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(EventHeader, eventName);
        if (!string.IsNullOrEmpty(effectiveSecret))
            request.Headers.TryAddWithoutValidation(SignatureHeader, $"sha256={ComputeSignature(effectiveSecret, jsonBody)}");

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Value.WebhookTimeout);
            using var response = await client.SendAsync(request, timeout.Token);
            var status = (int)response.StatusCode;
            return response.IsSuccessStatusCode
                ? new WebhookDeliveryResult(true, status, null)
                : new WebhookDeliveryResult(false, status, $"HTTP {status}");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Webhook delivery to {Url} failed", callbackUrl);
            return new WebhookDeliveryResult(false, null, ex.Message);
        }
    }

    public static string ComputeSignature(string secret, string body)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body));
        return Convert.ToHexStringLower(hash);
    }
}
