using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mycroft.Core.Interfaces;
using Mycroft.Core.Models;
using Mycroft.Infrastructure;
using Mycroft.Infrastructure.Inference;
using Mycroft.Infrastructure.Persistence;
using Mycroft.Infrastructure.Persistence.Seed;

namespace Mycroft.UnitTests;

/// <summary>Scripted IChatClient: the responder receives the messages and returns the assistant text.</summary>
internal sealed class FakeChatClient(Func<IReadOnlyList<ChatMessage>, string> responder) : IChatClient
{
    public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        Calls.Add(list);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responder(list))));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }

    /// <summary>Extracts the lineIndexes sent in the user prompt of a call.</summary>
    public static List<int> LineIndexes(IReadOnlyList<ChatMessage> messages)
    {
        var user = messages.First(m => m.Role == ChatRole.User).Text;
        var json = user[(user.IndexOf('[', StringComparison.Ordinal))..];
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(e => e.GetProperty("lineIndex").GetInt32()).ToList();
    }

    /// <param name="merchant">Merchant for every item; null gives each line its own ("Merchant {i}").</param>
    public static string ItemsJson(IEnumerable<int> indexes, string category = "Alimentação", string sub = "Delivery", decimal confidence = 0.95m, string? merchant = "iFood") =>
        JsonSerializer.Serialize(new
        {
            items = indexes.Select(i => new
            {
                lineIndex = i, merchantName = merchant ?? $"Merchant {i}", amount = -10.5m, currency = "BRL", transactionDate = "2026-10-02",
                transactionType = "Debit", category, subcategory = sub, paymentMethod = "CreditCard",
                installmentCurrent = (int?)null, installmentTotal = (int?)null, isRecurring = false, confidenceScore = confidence,
            }),
        });
}

internal static class TestFactory
{
    public static IOptions<MycroftOptions> Options(Action<MycroftOptions>? configure = null)
    {
        var o = new MycroftOptions { SlmChunkSize = 15, MaxConcurrentSlmRequests = 1 };
        configure?.Invoke(o);
        return Microsoft.Extensions.Options.Options.Create(o);
    }

    public static IReadOnlyList<TaxonomyEntry> DefaultTaxonomy =>
        CategorySeeder.Defaults.Select(d => new TaxonomyEntry(d.Name, d.Subcategories)).ToList();

    public static OllamaTransactionInferenceService Inference(IChatClient chat, IOptions<MycroftOptions>? options = null)
    {
        options ??= Options();
        return new OllamaTransactionInferenceService(chat, new SlmConcurrencyGate(options), new RegexFallbackExtractor(),
            options, NullLogger<OllamaTransactionInferenceService>.Instance);
    }

    public static async Task<MycroftDbContext> SeededDbAsync()
    {
        var options = new DbContextOptionsBuilder<MycroftDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var db = new MycroftDbContext(options);
        await CategorySeeder.SeedAsync(db);
        return db;
    }

    public static InferenceRequestItem Item(int index, string description, decimal? amount = -10.5m, bool freeText = false) =>
        new(new RawStatementLine(index, description, amount, new DateOnly(2026, 10, 2), IsFreeText: freeText),
            new MerchantNormalizer().Normalize(description));
}

internal sealed class FakeQueue : IReconciliationJobQueue
{
    public List<Guid> Enqueued { get; } = [];

    public ValueTask EnqueueAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        Enqueued.Add(jobId);
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<Guid> DequeueAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        foreach (var id in Enqueued) yield return id;
    }
}
