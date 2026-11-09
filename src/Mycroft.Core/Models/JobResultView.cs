using Mycroft.Core.Entities;
using Mycroft.Core.Enums;

namespace Mycroft.Core.Models;

/// <summary>Public representation of a job, shared by GET /reconciliations/{id} and the webhook payload.</summary>
public sealed record JobResultView
{
    public string? Event { get; init; }
    public Guid JobId { get; init; }
    public string? ExternalReferenceId { get; init; }
    public JobStatus Status { get; init; }
    public SourceFormat SourceFormat { get; init; }
    public string? OriginalFileName { get; init; }
    public string? ErrorMessage { get; init; }
    public required JobMetricsView Metrics { get; init; }
    public WebhookStatus WebhookStatus { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public IReadOnlyList<TransactionView>? Transactions { get; init; }

    public static JobResultView From(ReconciliationJob job, IEnumerable<TransactionItem>? items, string? eventName = null) => new()
    {
        Event = eventName,
        JobId = job.Id,
        ExternalReferenceId = job.ExternalReferenceId,
        Status = job.Status,
        SourceFormat = job.SourceFormat,
        OriginalFileName = job.OriginalFileName,
        ErrorMessage = job.ErrorMessage,
        WebhookStatus = job.WebhookStatus,
        CreatedAt = job.CreatedAt,
        CompletedAt = job.CompletedAt,
        Metrics = new JobMetricsView
        {
            TotalItems = job.TotalItems,
            ProcessedItems = job.ProcessedItems,
            AliasHits = job.AliasHits,
            CacheHits = job.CacheHits,
            FallbackItems = job.FallbackItems,
            DurationMs = job.StartedAt is { } s && job.CompletedAt is { } c ? (long)(c - s).TotalMilliseconds : null,
        },
        Transactions = items?.OrderBy(i => i.LineIndex).Select(TransactionView.From).ToList(),
    };
}

public sealed record JobMetricsView
{
    public int TotalItems { get; init; }
    public int ProcessedItems { get; init; }
    public int AliasHits { get; init; }
    public int CacheHits { get; init; }
    public int FallbackItems { get; init; }
    public long? DurationMs { get; init; }
}

public sealed record TransactionView
{
    public Guid Id { get; init; }
    public string? ExternalLineId { get; init; }
    public required string RawDescription { get; init; }
    public string? NormalizedSignature { get; init; }
    public string? MerchantName { get; init; }
    public decimal? Amount { get; init; }
    public string Currency { get; init; } = "BRL";
    public DateOnly? TransactionDate { get; init; }
    public TransactionType? TransactionType { get; init; }
    public string? Category { get; init; }
    public string? Subcategory { get; init; }
    public PaymentMethod? PaymentMethod { get; init; }
    public int? InstallmentCurrent { get; init; }
    public int? InstallmentTotal { get; init; }
    public bool IsRecurring { get; init; }
    public decimal ConfidenceScore { get; init; }
    public bool NeedsReview { get; init; }
    public ResolutionSource? ResolutionSource { get; init; }

    public static TransactionView From(TransactionItem i) => new()
    {
        Id = i.Id,
        ExternalLineId = i.ExternalLineId,
        RawDescription = i.RawDescription,
        NormalizedSignature = i.NormalizedSignature,
        MerchantName = i.MerchantName,
        Amount = i.Amount,
        Currency = i.Currency,
        TransactionDate = i.TransactionDate,
        TransactionType = i.TransactionType,
        Category = i.Category,
        Subcategory = i.Subcategory,
        PaymentMethod = i.PaymentMethod,
        InstallmentCurrent = i.InstallmentCurrent,
        InstallmentTotal = i.InstallmentTotal,
        IsRecurring = i.IsRecurring,
        ConfidenceScore = i.ConfidenceScore,
        NeedsReview = i.NeedsReview,
        ResolutionSource = i.ResolutionSource,
    };
}
