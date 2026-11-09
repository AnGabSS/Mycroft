using Microsoft.EntityFrameworkCore;
using Mycroft.Core.Entities;
using Mycroft.Core.Enums;
using Mycroft.Infrastructure.Persistence;

namespace Mycroft.Infrastructure.Processing;

public sealed record TransactionFeedback(
    string? MerchantName,
    string? Category,
    string? Subcategory,
    PaymentMethod? PaymentMethod,
    bool? IsRecurring);

public enum FeedbackOutcome { NotFound, Invalid, Updated }

public sealed record FeedbackResult(FeedbackOutcome Outcome, TransactionItem? Item = null, string? Error = null, bool PromotedFullPattern = false);

/// <summary>
/// Human corrections. Default-taxonomy categories are promoted as a full cache pattern (confidence 1.0);
/// tenant-private categories only teach merchant metadata, keeping the global cache free of private data.
/// </summary>
public sealed class FeedbackService(MycroftDbContext db, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<FeedbackResult> ApplyAsync(Guid transactionId, TransactionFeedback feedback, CancellationToken ct)
    {
        var item = await db.TransactionItems.Include(i => i.Job).FirstOrDefaultAsync(i => i.Id == transactionId, ct);
        if (item is null) return new FeedbackResult(FeedbackOutcome.NotFound);

        var defaultCategory = feedback.Category is null
            ? null
            : await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Name == feedback.Category, ct);

        var customCategories = MycroftJson.DeserializeCategories(item.Job?.CustomCategoriesJson);
        var customCategory = feedback.Category is null ? null : customCategories.FirstOrDefault(c => c.Name == feedback.Category);

        if (feedback.Category is not null && defaultCategory is null && customCategory is null)
            return new FeedbackResult(FeedbackOutcome.Invalid, Error: $"Category '{feedback.Category}' is neither in the default taxonomy nor in this job's custom categories.");

        if (feedback.Subcategory is not null && feedback.Category is not null)
        {
            var allowed = (defaultCategory?.Subcategories ?? []).Concat(customCategory?.Subcategories ?? []).ToHashSet();
            if (allowed.Count > 0 && !allowed.Contains(feedback.Subcategory))
                return new FeedbackResult(FeedbackOutcome.Invalid, Error: $"Subcategory '{feedback.Subcategory}' does not belong to '{feedback.Category}'.");
        }

        if (feedback.MerchantName is not null) item.MerchantName = feedback.MerchantName.Trim();
        if (feedback.Category is not null)
        {
            item.Category = feedback.Category;
            item.Subcategory = feedback.Subcategory;
        }
        else if (feedback.Subcategory is not null)
        {
            item.Subcategory = feedback.Subcategory;
        }
        if (feedback.PaymentMethod is not null) item.PaymentMethod = feedback.PaymentMethod;
        if (feedback.IsRecurring is not null) item.IsRecurring = feedback.IsRecurring.Value;
        item.ConfidenceScore = 1.000m;
        item.NeedsReview = false;

        var promotedFull = false;
        if (item.NormalizedSignature.Length > 0 && item.MerchantName is not null)
        {
            // The (final) category must exist in the default taxonomy — with a compatible subcategory — to enter the global cache.
            var taxonomyCategory = defaultCategory ?? (item.Category is null
                ? null
                : await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Name == item.Category, ct));
            var isDefaultCategory = taxonomyCategory is not null &&
                                    (item.Subcategory is null || taxonomyCategory.Subcategories.Contains(item.Subcategory));

            var pattern = await db.MerchantPatterns.FirstOrDefaultAsync(p => p.NormalizedSignature == item.NormalizedSignature, ct);
            if (pattern is null)
            {
                pattern = new MerchantPattern { NormalizedSignature = item.NormalizedSignature, MerchantName = item.MerchantName };
                db.MerchantPatterns.Add(pattern);
            }

            pattern.MerchantName = item.MerchantName;
            pattern.DefaultPaymentMethod = item.PaymentMethod;
            pattern.IsRecurring = item.IsRecurring;
            pattern.UpdatedAt = _time.GetUtcNow();
            if (isDefaultCategory)
            {
                pattern.Category = item.Category;
                pattern.Subcategory = item.Subcategory;
                pattern.ConfidenceScore = 1.000m;
                promotedFull = true;
            }
            else if (pattern.Category is null)
            {
                pattern.ConfidenceScore = 1.000m; // merchant-only knowledge, used as a hint
            }
        }

        await db.SaveChangesAsync(ct);
        return new FeedbackResult(FeedbackOutcome.Updated, item, PromotedFullPattern: promotedFull);
    }
}
