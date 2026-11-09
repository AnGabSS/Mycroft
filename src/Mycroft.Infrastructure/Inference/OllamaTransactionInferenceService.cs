using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mycroft.Core.Enums;
using Mycroft.Core.Interfaces;
using Mycroft.Core.Models;
using Mycroft.Core.Services;

namespace Mycroft.Infrastructure.Inference;

/// <summary>
/// SLM enrichment through <see cref="IChatClient"/> (Ollama by default) with structured outputs and a
/// 3-level fallback so that a single hallucinated line never fails a job:
/// <list type="number">
/// <item>Chunk call; invalid/missing items get one repair retry that includes the validation error (Auto-Repair).</item>
/// <item>Items still invalid are sent one by one (isolation).</item>
/// <item>Items still invalid go to <see cref="RegexFallbackExtractor"/> (confidence 0.2, needs review).</item>
/// </list>
/// </summary>
public sealed class OllamaTransactionInferenceService(
    IChatClient chatClient,
    SlmConcurrencyGate gate,
    RegexFallbackExtractor fallback,
    IOptions<MycroftOptions> options,
    ILogger<OllamaTransactionInferenceService> logger) : ITransactionInferenceService
{
    private readonly MycroftOptions _options = options.Value;

    /// <summary>Confidence cap for answers whose merchant is not supported by the description.</summary>
    internal const decimal UngroundedMaxConfidence = 0.500m;

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public async Task<IReadOnlyList<ReconciledTransactionResult>> InferAsync(
        IReadOnlyList<InferenceRequestItem> items, InferenceContext context, CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<int, ReconciledTransactionResult>(items.Count);
        var schema = PromptTemplates.BuildSchema(context.Taxonomy);
        var systemPrompt = PromptTemplates.SystemPrompt(context.Taxonomy);

        foreach (var chunk in items.Chunk(Math.Max(1, _options.SlmChunkSize)))
        {
            var remaining = chunk.ToList();

            // Level 0 + Level 1 (repair retry with the validation error)
            var first = await CallAsync(remaining, context, schema, systemPrompt, repair: null, cancellationToken);
            Accept(first, remaining, results, ResolutionSource.SlmBatch);

            if (remaining.Count > 0 && !first.TransportFailure)
            {
                logger.LogInformation("SLM chunk had {Count} invalid items, retrying with repair prompt: {Error}", remaining.Count, first.Error);
                var repaired = await CallAsync(remaining, context, schema, systemPrompt, (first.RawText, first.Error ?? "itens ausentes"), cancellationToken);
                Accept(repaired, remaining, results, ResolutionSource.SlmBatch);
                first = repaired;
            }

            // Level 2: isolate items one by one
            if (remaining.Count > 0 && !first.TransportFailure)
            {
                foreach (var item in remaining.ToList())
                {
                    var single = await CallAsync([item], context, schema, systemPrompt, repair: null, cancellationToken);
                    var list = new List<InferenceRequestItem> { item };
                    Accept(single, list, results, ResolutionSource.SlmSingleRetry);
                    if (list.Count == 0) remaining.Remove(item);
                    if (single.TransportFailure) break;
                }
            }

            // Level 3: deterministic regex fallback — never fails
            foreach (var item in remaining)
            {
                results[item.Line.LineIndex] = fallback.Extract(item);
            }
        }

        return items.Select(i => results[i.Line.LineIndex]).ToList();
    }

    private void Accept(CallResult call, List<InferenceRequestItem> remaining, Dictionary<int, ReconciledTransactionResult> results, ResolutionSource source)
    {
        foreach (var item in remaining.ToList())
        {
            if (call.Valid.TryGetValue(item.Line.LineIndex, out var slm))
            {
                results[item.Line.LineIndex] = Merge(item, slm, source);
                remaining.Remove(item);
            }
        }
    }

    /// <summary>Structural data from the source always wins over the model; deterministic hints override the model.</summary>
    private ReconciledTransactionResult Merge(InferenceRequestItem item, ReconciledTransactionResult slm, ResolutionSource source)
    {
        var line = item.Line;
        var amount = line.RawAmount ?? slm.Amount;
        // A structural amount is signed by contract, so its sign decides; otherwise trust the model.
        var type = line.RawAmount is { } structural
            ? RegexFallbackExtractor.TypeFromSign(structural)
            : amount is < 0 ? TransactionType.Debit : slm.TransactionType;

        // Small models often ignore installment markers ("PARC 01/03"); the deterministic regex fills the gap.
        var (installmentCurrent, installmentTotal) = slm.InstallmentCurrent is not null && slm.InstallmentTotal is not null
            ? (slm.InstallmentCurrent, slm.InstallmentTotal)
            : RegexFallbackExtractor.ExtractInstallments(line.RawDescription);

        // Explicit keywords (PIX, TED/DOC, BOLETO, SAQUE) state the payment method; the model does not get a vote.
        var upperDescription = TextNormalization.ToUpperAsciiTokens(line.RawDescription);
        var paymentMethod = RegexFallbackExtractor.StrongPaymentMethod(upperDescription) ?? slm.PaymentMethod;

        var result = slm with
        {
            Amount = amount,
            TransactionDate = line.RawDate ?? slm.TransactionDate,
            TransactionType = type,
            PaymentMethod = paymentMethod,
            InstallmentCurrent = installmentCurrent,
            InstallmentTotal = installmentTotal,
            Source = source,
        };

        // A merchant unrelated to the description leaked from another line (or was invented): fall back to the
        // cleaned description and send the item to review. Merchants imposed by a hint are trusted.
        if (item.Hint?.MerchantName is null && result.MerchantName is { } merchant)
        {
            merchant = MerchantGrounding.StripInventedPrefixes(merchant, line.RawDescription);
            result = MerchantGrounding.IsGrounded(merchant, line.RawDescription, item.NormalizedSignature)
                ? result with { MerchantName = merchant }
                : result with
                {
                    MerchantName = PtBr.TextInfo.ToTitleCase(item.NormalizedSignature.ToLowerInvariant()),
                    ConfidenceScore = Math.Min(result.ConfidenceScore, UngroundedMaxConfidence),
                };
        }

        if (item.Hint is { } hint)
        {
            result = result with
            {
                MerchantName = hint.MerchantName ?? result.MerchantName,
                Category = hint.Category ?? result.Category,
                Subcategory = hint.Category is not null ? hint.Subcategory : result.Subcategory,
            };
        }

        return result with { NeedsReview = result.ConfidenceScore < _options.NeedsReviewThreshold };
    }

    private sealed record CallResult(Dictionary<int, ReconciledTransactionResult> Valid, string? Error, string? RawText, bool TransportFailure);

    private async Task<CallResult> CallAsync(
        IReadOnlyList<InferenceRequestItem> items,
        InferenceContext context,
        JsonElement schema,
        string systemPrompt,
        (string? PreviousResponse, string Error)? repair,
        CancellationToken ct)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, PromptTemplates.UserPrompt(items)),
        };
        if (repair is { } r)
        {
            if (!string.IsNullOrEmpty(r.PreviousResponse)) messages.Add(new ChatMessage(ChatRole.Assistant, r.PreviousResponse));
            messages.Add(new ChatMessage(ChatRole.User, PromptTemplates.RepairPrompt(r.Error)));
        }

        var chatOptions = new ChatOptions
        {
            Temperature = 0f,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(schema, "reconciled_transactions", "Structured enrichment of raw financial transactions"),
        };

        string text;
        try
        {
            using var _ = await gate.AcquireAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.SlmRequestTimeout);
            var response = await chatClient.GetResponseAsync(messages, chatOptions, timeout.Token);
            text = response.Text;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("SLM call timed out after {Timeout}", _options.SlmRequestTimeout);
            return new CallResult([], "timeout", null, TransportFailure: false);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "SLM endpoint unavailable");
            return new CallResult([], ex.Message, null, TransportFailure: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "SLM call failed");
            return new CallResult([], ex.Message, null, TransportFailure: false);
        }

        var (valid, error) = SlmResponseValidator.Validate(text, items, context.Taxonomy);
        return new CallResult(valid, error, text, TransportFailure: false);
    }
}

/// <summary>Parses and validates the model output. Soft issues are corrected, hard issues reported for repair.</summary>
internal static class SlmResponseValidator
{
    public static (Dictionary<int, ReconciledTransactionResult> Valid, string? Error) Validate(
        string? text, IReadOnlyList<InferenceRequestItem> expected, IReadOnlyList<TaxonomyEntry> taxonomy)
    {
        var valid = new Dictionary<int, ReconciledTransactionResult>();
        var errors = new List<string>();
        var expectedIndexes = expected.Select(e => e.Line.LineIndex).ToHashSet();

        JsonElement itemsArray;
        try
        {
            using var doc = JsonDocument.Parse(StripCodeFences(text ?? string.Empty));
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
                itemsArray = arr.Clone();
            else if (root.ValueKind == JsonValueKind.Array)
                itemsArray = root.Clone();
            else
                return (valid, "o JSON deve ser um objeto com a propriedade 'items' (array)");
        }
        catch (JsonException ex)
        {
            return (valid, $"JSON inválido: {ex.Message}");
        }

        foreach (var el in itemsArray.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) { errors.Add("item não é um objeto"); continue; }

            if (!TryGetInt(el, "lineIndex", out var index) || !expectedIndexes.Contains(index))
            {
                errors.Add("lineIndex ausente ou desconhecido");
                continue;
            }
            if (valid.ContainsKey(index)) continue;

            var merchant = GetString(el, "merchantName");
            if (string.IsNullOrWhiteSpace(merchant)) { errors.Add($"lineIndex {index}: merchantName vazio"); continue; }

            var categoryRaw = GetString(el, "category");
            var category = taxonomy.FirstOrDefault(t => string.Equals(t.Name, categoryRaw?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (category is null) { errors.Add($"lineIndex {index}: category '{categoryRaw}' não pertence à taxonomia"); continue; }

            var subRaw = GetString(el, "subcategory")?.Trim();
            var subcategory = category.Subcategories.FirstOrDefault(s => string.Equals(s, subRaw, StringComparison.OrdinalIgnoreCase));

            TransactionType? type = Enum.TryParse<TransactionType>(GetString(el, "transactionType"), true, out var tt) ? tt : null;
            var payment = Enum.TryParse<PaymentMethod>(GetString(el, "paymentMethod"), true, out var pm) ? pm : PaymentMethod.Other;

            var confidence = TryGetDecimal(el, "confidenceScore", out var conf) ? Math.Clamp(conf, 0m, 1m) : 0.5m;
            int? instCur = TryGetInt(el, "installmentCurrent", out var ic) ? ic : null;
            int? instTot = TryGetInt(el, "installmentTotal", out var it) ? it : null;
            if (instCur is null || instTot is null || instCur < 1 || instTot < 2 || instCur > instTot) { instCur = null; instTot = null; }

            var currency = GetString(el, "currency")?.Trim().ToUpperInvariant();

            valid[index] = new ReconciledTransactionResult
            {
                LineIndex = index,
                MerchantName = Truncate(merchant.Trim(), 200),
                Amount = TryGetDecimal(el, "amount", out var amt) ? Math.Round(amt, 2) : null,
                Currency = currency is { Length: 3 } ? currency : "BRL",
                TransactionDate = DateOnly.TryParseExact(GetString(el, "transactionDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null,
                TransactionType = type,
                Category = category.Name,
                Subcategory = subcategory,
                PaymentMethod = payment,
                InstallmentCurrent = instCur,
                InstallmentTotal = instTot,
                IsRecurring = el.TryGetProperty("isRecurring", out var rec) && rec.ValueKind == JsonValueKind.True,
                ConfidenceScore = Math.Round(confidence, 3),
            };
        }

        RejectRepeatedMerchants(valid, expected, errors);

        var missing = expectedIndexes.Except(valid.Keys).Order().ToList();
        if (missing.Count > 0) errors.Add($"faltam itens para lineIndex: {string.Join(", ", missing)}");

        return (valid, errors.Count == 0 ? null : string.Join("; ", errors.Distinct()));
    }

    /// <summary>
    /// Small models sometimes copy one answer down the whole list ("iFood" for PIX transfers, IOF, interest...).
    /// When the same merchant is given to 3+ different signatures and covers at least a third of the answers,
    /// those items are rejected so the repair/isolation levels re-ask them. Merchants imposed by a hint are exempt,
    /// and single-item calls (isolation) are never flagged.
    /// </summary>
    internal const int RepeatedMerchantMinItems = 3;

    private static void RejectRepeatedMerchants(
        Dictionary<int, ReconciledTransactionResult> valid, IReadOnlyList<InferenceRequestItem> expected, List<string> errors)
    {
        if (valid.Count < 4) return;

        var byIndex = expected.ToDictionary(e => e.Line.LineIndex);
        var suspicious = valid.Values
            .Where(v => !string.Equals(byIndex[v.LineIndex].Hint?.MerchantName, v.MerchantName, StringComparison.OrdinalIgnoreCase))
            .GroupBy(v => v.MerchantName!.Trim().ToUpperInvariant())
            .Where(g => g.Select(v => byIndex[v.LineIndex].NormalizedSignature).Distinct().Count() >= RepeatedMerchantMinItems
                        && g.Count() * 3 >= valid.Count)
            .ToList();

        foreach (var group in suspicious)
        {
            var indexes = group.Select(v => v.LineIndex).Order().ToList();
            foreach (var i in indexes) valid.Remove(i);
            errors.Add($"o mesmo merchantName '{group.First().MerchantName}' foi repetido para descrições diferentes (lineIndex {string.Join(", ", indexes)}); analise cada descrição separadamente");
        }
    }

    internal static string StripCodeFences(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith("```", StringComparison.Ordinal)) return t;
        var firstNewLine = t.IndexOf('\n');
        var lastFence = t.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewLine > 0 && lastFence > firstNewLine ? t[(firstNewLine + 1)..lastFence].Trim() : t.Trim('`');
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool TryGetInt(JsonElement el, string name, out int value)
    {
        value = 0;
        if (!el.TryGetProperty(name, out var p)) return false;
        if (p.ValueKind == JsonValueKind.Number) return p.TryGetInt32(out value);
        return p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetDecimal(JsonElement el, string name, out decimal value)
    {
        value = 0;
        if (!el.TryGetProperty(name, out var p)) return false;
        if (p.ValueKind == JsonValueKind.Number) return p.TryGetDecimal(out value);
        return p.ValueKind == JsonValueKind.String && decimal.TryParse(p.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
