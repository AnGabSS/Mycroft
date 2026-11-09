using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mycroft.Core.Interfaces;
using Testcontainers.PostgreSql;

namespace Mycroft.IntegrationTests;

/// <summary>
/// Boots the real API against a throwaway PostgreSQL 17 container. The SLM and the webhook transport are
/// replaced with deterministic fakes; everything else (EF, migrations, channel, workers) is real.
/// </summary>
public sealed class MycroftApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;

    public const string ApiKey = "integration-test-key";

    public bool DockerAvailable { get; private set; }
    public string? DockerError { get; private set; }
    public RecordingWebhookDispatcher Webhooks { get; } = new();
    public FakeSlm Slm { get; } = new();

    public async Task InitializeAsync()
    {
        try
        {
            // Building the container already probes Docker, so it must live inside the try.
            _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await _postgres.StartAsync();
            DockerAvailable = true;
        }
        catch (Exception ex)
        {
            DockerError = ex.Message;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Mycroft", _postgres?.GetConnectionString() ?? "Host=unavailable");
        builder.UseSetting("SLM_CHUNK_SIZE", "5");
        builder.UseSetting("SLM_WARMUP", "false");
        builder.UseSetting("MYCROFT_API_KEY", ApiKey);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IChatClient>();
            services.AddSingleton<IChatClient>(Slm);
            services.RemoveAll<IWebhookDispatcher>();
            services.AddSingleton<IWebhookDispatcher>(Webhooks);
        });
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
    }
}

/// <summary>Answers every line as "Alimentação/Delivery" with high confidence.</summary>
public sealed class FakeSlm : IChatClient
{
    public int Calls;

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Calls);
        var user = messages.First(m => m.Role == ChatRole.User).Text;
        using var doc = JsonDocument.Parse(user[user.IndexOf('[', StringComparison.Ordinal)..]);
        var items = doc.RootElement.EnumerateArray().Select(e => new
        {
            lineIndex = e.GetProperty("lineIndex").GetInt32(),
            merchantName = "Merchant " + e.GetProperty("lineIndex").GetInt32(),
            amount = e.TryGetProperty("amount", out var a) ? a.GetDecimal() : -1m,
            currency = "BRL",
            transactionDate = "2026-10-02",
            transactionType = "Debit",
            category = "Alimentação",
            subcategory = "Delivery",
            paymentMethod = "CreditCard",
            installmentCurrent = (int?)null,
            installmentTotal = (int?)null,
            isRecurring = false,
            confidenceScore = 0.95,
        });
        var json = JsonSerializer.Serialize(new { items });
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

public sealed class RecordingWebhookDispatcher : IWebhookDispatcher
{
    public ConcurrentBag<(string Url, string? Secret, string Event, string Body)> Deliveries { get; } = [];

    public Task<WebhookDeliveryResult> DispatchAsync(string callbackUrl, string? secret, string eventName, string jsonBody, CancellationToken cancellationToken = default)
    {
        Deliveries.Add((callbackUrl, secret, eventName, jsonBody));
        return Task.FromResult(new WebhookDeliveryResult(true, 200, null));
    }
}
