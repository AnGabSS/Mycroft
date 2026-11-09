using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mycroft.Core.Entities;
using Mycroft.Core.Enums;
using Mycroft.Core.Interfaces;
using Mycroft.Core.Models;
using Mycroft.Core.Services;
using Mycroft.Infrastructure.Inference;
using Mycroft.Infrastructure.Persistence;

namespace Mycroft.Infrastructure.Processing;

/// <summary>
/// Runs the reconciliation pipeline for one job: parse → normalize → alias (job scope) → global cache → SLM.
/// Progress is checkpointed per slice so a crash resumes only the unprocessed items.
/// </summary>
public sealed class ReconciliationProcessor(
    MycroftDbContext db,
    IStatementParserFactory parsers,
    IMerchantNormalizer normalizer,
    ITransactionInferenceService inference,
    IOptions<MycroftOptions> options,
    ILogger<ReconciliationProcessor> logger,
    TimeProvider? timeProvider = null)
{
    private readonly MycroftOptions _options = options.Value;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task ProcessAsync(Guid jobId, CancellationToken ct)
    {
        var job = await db.ReconciliationJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || job.IsFinished) return;

        if (job.Status == JobStatus.Pending)
        {
            job.Status = JobStatus.Processing;
            job.StartedAt = _time.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }

        try
        {
            if (!await EnsureItemsExtractedAsync(job, ct)) return;

            var customCategories = MycroftJson.DeserializeCategories(job.CustomCategoriesJson);
            var defaultTaxonomy = await LoadDefaultTaxonomyAsync(ct);
            var taxonomy = customCategories.Count > 0
                ? customCategories.Select(c => new TaxonomyEntry(c.Name, c.Subcategories)).ToList()
                : defaultTaxonomy;
            var ctx = new PipelineContext(new AliasMatcher(customCategories), taxonomy, defaultTaxonomy.Select(t => t.Name).ToHashSet());

            var sliceSize = Math.Max(1, _options.SlmChunkSize) * 4;
            while (true)
            {
                var slice = await db.TransactionItems
                    .Where(i => i.JobId == job.Id && i.ProcessedAt == null)
                    .OrderBy(i => i.LineIndex)
                    .Take(sliceSize)
                    .ToListAsync(ct);
                if (slice.Count == 0) break;

                await ProcessSliceAsync(slice, ctx, ct);
                job.ProcessedItems += slice.Count;
                await db.SaveChangesAsync(ct); // checkpoint
            }

            await FinalizeAsync(job, error: null, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // shutdown: job stays Processing and is recovered on next boot
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobId} failed", job.Id);
            db.ChangeTracker.Clear();
            var fresh = await db.ReconciliationJobs.FirstAsync(j => j.Id == jobId, ct);
            await FinalizeAsync(fresh, $"Unexpected processing error: {ex.Message}", ct, failed: true);
        }
    }

    private sealed record PipelineContext(AliasMatcher Aliases, IReadOnlyList<TaxonomyEntry> Taxonomy, HashSet<string> DefaultCategoryNames);

    /// <summary>Parses the stored file into TransactionItems (once). Returns false if the job was finalized as failed.</summary>
    private async Task<bool> EnsureItemsExtractedAsync(ReconciliationJob job, CancellationToken ct)
    {
        if (job.SourceFormat == SourceFormat.JsonBatch || job.RawFilePayload is null) return true;
        if (await db.TransactionItems.AnyAsync(i => i.JobId == job.Id, ct)) return true;

        IReadOnlyList<RawStatementLine> lines;
        try
        {
            lines = parsers.Get(job.SourceFormat).Parse(job.RawFilePayload);
        }
        catch (StatementParseException ex)
        {
            await FinalizeAsync(job, ex.Message, ct, failed: true);
            return false;
        }

        if (lines.Count > _options.MaxItemsPerJob)
        {
            await FinalizeAsync(job, $"File contains {lines.Count} transactions; the limit is MAX_ITEMS_PER_JOB={_options.MaxItemsPerJob}.", ct, failed: true);
            return false;
        }

        foreach (var line in lines)
            db.TransactionItems.Add(CreateItem(job.Id, line, normalizer));

        job.TotalItems = lines.Count;
        await db.SaveChangesAsync(ct); // one transaction: all lines or none
        return true;
    }

    public static TransactionItem CreateItem(Guid jobId, RawStatementLine line, IMerchantNormalizer normalizer) => new()
    {
        JobId = jobId,
        LineIndex = line.LineIndex,
        ExternalLineId = line.ExternalLineId is { Length: > 100 } id ? id[..100] : line.ExternalLineId,
        RawDescription = line.RawDescription,
        NormalizedSignature = normalizer.Normalize(line.RawDescription),
        RawAmount = line.RawAmount,
        RawDate = line.RawDate,
        IsFreeText = line.IsFreeText,
    };

    private async Task ProcessSliceAsync(List<TransactionItem> slice, PipelineContext ctx, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var hints = new Dictionary<Guid, InferenceHint>();
        var pending = new List<TransactionItem>();

        // Layer 2 lookup is batched for the whole slice.
        var signatures = slice.Select(i => i.NormalizedSignature).Where(s => s.Length > 0).Distinct().ToList();
        var patterns = await db.MerchantPatterns
            .Where(p => signatures.Contains(p.NormalizedSignature))
            .ToDictionaryAsync(p => p.NormalizedSignature, ct);

        foreach (var item in slice)
        {
            // Layer 1 — job-scoped aliases (longest match wins)
            var alias = ctx.Aliases.Match(item.NormalizedSignature);
            if (alias is not null)
            {
                var merchant = alias.Alias.MerchantName ?? Title(alias.CleanPattern);
                if (!item.IsFreeText && item.RawAmount is not null)
                {
                    ApplyDeterministic(item, merchant, alias.CategoryName, alias.Alias.Subcategory, null, false, 1.000m, ResolutionSource.AliasMatch, now);
                    continue;
                }
                hints[item.Id] = new InferenceHint(merchant, alias.CategoryName, alias.Alias.Subcategory);
                pending.Add(item);
                continue;
            }

            // Layer 2 — global learned cache
            if (patterns.TryGetValue(item.NormalizedSignature, out var pattern))
            {
                var compatible = pattern.Category is not null
                                 && pattern.ConfidenceScore >= MerchantPattern.MinCacheConfidence
                                 && ctx.Taxonomy.Any(t => t.Name == pattern.Category
                                                          && (pattern.Subcategory is null || t.Subcategories.Contains(pattern.Subcategory)));

                if (compatible && !item.IsFreeText && item.RawAmount is not null)
                {
                    ApplyDeterministic(item, pattern.MerchantName, pattern.Category!, pattern.Subcategory, pattern.DefaultPaymentMethod,
                        pattern.IsRecurring, pattern.ConfidenceScore, ResolutionSource.Cache, now);
                    pattern.HitCount++;
                    pattern.UpdatedAt = now;
                    continue;
                }

                hints[item.Id] = new InferenceHint(pattern.MerchantName, compatible ? pattern.Category : null, compatible ? pattern.Subcategory : null);
            }

            pending.Add(item);
        }

        if (pending.Count == 0) return;

        // Layer 3 — SLM. Structured lines with the same signature are sent once and the result is reused.
        var representatives = new Dictionary<string, TransactionItem>();
        var requests = new List<InferenceRequestItem>();
        foreach (var item in pending)
        {
            var key = item.IsFreeText || item.NormalizedSignature.Length == 0 ? $"#{item.Id}" : item.NormalizedSignature;
            if (representatives.ContainsKey(key)) continue;
            representatives[key] = item;
            hints.TryGetValue(item.Id, out var hint);
            requests.Add(new InferenceRequestItem(ToLine(item), item.NormalizedSignature, hint));
        }

        var results = (await inference.InferAsync(requests, new InferenceContext(ctx.Taxonomy), ct))
            .ToDictionary(r => r.LineIndex);

        foreach (var item in pending)
        {
            var key = item.IsFreeText || item.NormalizedSignature.Length == 0 ? $"#{item.Id}" : item.NormalizedSignature;
            var rep = representatives[key];
            var result = results[rep.LineIndex];
            ApplyInference(item, rep, result, now);
        }

        if (_options.SlmAutoLearn) LearnPatterns(representatives.Values, results, patterns, ctx, now);
    }

    private static RawStatementLine ToLine(TransactionItem i) =>
        new(i.LineIndex, i.RawDescription, i.RawAmount, i.RawDate, i.ExternalLineId, i.IsFreeText);

    private static void ApplyDeterministic(TransactionItem item, string merchant, string category, string? subcategory,
        PaymentMethod? defaultPayment, bool isRecurring, decimal confidence, ResolutionSource source, DateTimeOffset now)
    {
        var upper = TextNormalization.ToUpperAsciiTokens(item.RawDescription);
        var (cur, tot) = RegexFallbackExtractor.ExtractInstallments(item.RawDescription);

        item.MerchantName = merchant;
        item.Category = category;
        item.Subcategory = subcategory;
        item.Amount = item.RawAmount;
        item.TransactionDate = item.RawDate;
        item.TransactionType = RegexFallbackExtractor.TypeFromSign(item.RawAmount!.Value);
        item.PaymentMethod = defaultPayment ?? RegexFallbackExtractor.InferPaymentMethod(upper);
        item.InstallmentCurrent = cur;
        item.InstallmentTotal = tot;
        item.IsRecurring = isRecurring;
        item.ConfidenceScore = confidence;
        item.NeedsReview = false;
        item.ResolutionSource = source;
        item.ProcessedAt = now;
    }

    private static void ApplyInference(TransactionItem item, TransactionItem representative, ReconciledTransactionResult r, DateTimeOffset now)
    {
        var isRepresentative = ReferenceEquals(item, representative);
        var amount = isRepresentative ? r.Amount : item.RawAmount ?? r.Amount;

        item.MerchantName = r.MerchantName;
        item.Amount = amount;
        item.Currency = r.Currency;
        item.TransactionDate = isRepresentative ? r.TransactionDate : item.RawDate ?? r.TransactionDate;
        item.TransactionType = item.RawAmount is { } structural
            ? RegexFallbackExtractor.TypeFromSign(structural)
            : amount is < 0 ? TransactionType.Debit : r.TransactionType;
        item.Category = r.Category;
        item.Subcategory = r.Subcategory;
        item.PaymentMethod = r.PaymentMethod;
        if (isRepresentative)
        {
            item.InstallmentCurrent = r.InstallmentCurrent;
            item.InstallmentTotal = r.InstallmentTotal;
        }
        else
        {
            (item.InstallmentCurrent, item.InstallmentTotal) = RegexFallbackExtractor.ExtractInstallments(item.RawDescription);
        }
        item.IsRecurring = r.IsRecurring;
        item.ConfidenceScore = r.ConfidenceScore;
        item.NeedsReview = r.NeedsReview;
        item.ResolutionSource = r.Source;
        item.ProcessedAt = now;
    }

    /// <summary>
    /// Opt-in (SLM_AUTO_LEARN): high-confidence SLM answers in the default taxonomy feed the global cache
    /// (never tenant-private categories).
    /// </summary>
    private void LearnPatterns(IEnumerable<TransactionItem> representatives, Dictionary<int, ReconciledTransactionResult> results,
        Dictionary<string, MerchantPattern> patterns, PipelineContext ctx, DateTimeOffset now)
    {
        foreach (var item in representatives)
        {
            var r = results[item.LineIndex];
            if (r.Source is not (ResolutionSource.SlmBatch or ResolutionSource.SlmSingleRetry)) continue;
            if (r.ConfidenceScore < MerchantPattern.MinCacheConfidence || r.MerchantName is null || r.Category is null) continue;
            if (item.NormalizedSignature.Length == 0 || !ctx.DefaultCategoryNames.Contains(r.Category)) continue;

            if (patterns.TryGetValue(item.NormalizedSignature, out var existing))
            {
                if (existing.Category is not null && existing.ConfidenceScore >= r.ConfidenceScore) continue;
                existing.MerchantName = r.MerchantName;
                existing.Category = r.Category;
                existing.Subcategory = r.Subcategory;
                existing.DefaultPaymentMethod = r.PaymentMethod;
                existing.IsRecurring = r.IsRecurring;
                existing.ConfidenceScore = r.ConfidenceScore;
                existing.UpdatedAt = now;
            }
            else
            {
                var pattern = new MerchantPattern
                {
                    NormalizedSignature = item.NormalizedSignature,
                    MerchantName = r.MerchantName,
                    Category = r.Category,
                    Subcategory = r.Subcategory,
                    DefaultPaymentMethod = r.PaymentMethod,
                    IsRecurring = r.IsRecurring,
                    ConfidenceScore = r.ConfidenceScore,
                    UpdatedAt = now,
                };
                patterns[pattern.NormalizedSignature] = pattern;
                db.MerchantPatterns.Add(pattern);
            }
        }
    }

    private async Task FinalizeAsync(ReconciliationJob job, string? error, CancellationToken ct, bool failed = false)
    {
        var items = db.TransactionItems.Where(i => i.JobId == job.Id);
        job.TotalItems = await items.CountAsync(ct);
        job.ProcessedItems = await items.CountAsync(i => i.ProcessedAt != null, ct);
        job.AliasHits = await items.CountAsync(i => i.ResolutionSource == ResolutionSource.AliasMatch, ct);
        job.CacheHits = await items.CountAsync(i => i.ResolutionSource == ResolutionSource.Cache, ct);
        job.FallbackItems = await items.CountAsync(i => i.ResolutionSource == ResolutionSource.RegexFallback, ct);
        job.ErrorMessage = error ?? (job.TotalItems == 0 ? "No transactions found in the input." : null);
        job.Status = failed
            ? JobStatus.Failed
            : job.FallbackItems > 0 || job.TotalItems == 0 ? JobStatus.CompletedWithWarnings : JobStatus.Completed;
        job.CompletedAt = _time.GetUtcNow();
        job.StartedAt ??= job.CompletedAt;

        // Lines are durable in TransactionItems now — drop the raw bytes. Failed jobs keep them for diagnosis until retention.
        if (!failed) job.RawFilePayload = null;

        if (!string.IsNullOrWhiteSpace(job.CallbackUrl))
        {
            job.WebhookStatus = WebhookStatus.Pending;
            job.WebhookAttempts = 0;
            job.NextWebhookRetryAt = job.CompletedAt;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Job {JobId} finished as {Status}: {Total} items, {Alias} alias, {Cache} cache, {Fallback} fallback",
            job.Id, job.Status, job.TotalItems, job.AliasHits, job.CacheHits, job.FallbackItems);
    }

    private async Task<IReadOnlyList<TaxonomyEntry>> LoadDefaultTaxonomyAsync(CancellationToken ct) =>
        (await db.Categories.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct))
        .Select(c => new TaxonomyEntry(c.Name, c.Subcategories))
        .ToList();

    private static string Title(string s) => CultureInfo.GetCultureInfo("pt-BR").TextInfo.ToTitleCase(s.ToLowerInvariant());
}
