using Mycroft.Core.Enums;

namespace Mycroft.Core.Entities;

public sealed class TransactionItem
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid JobId { get; set; }
    public ReconciliationJob? Job { get; set; }

    /// <summary>Position of the line in the original batch/file (stable ordering).</summary>
    public int LineIndex { get; set; }
    public string? ExternalLineId { get; set; }

    public required string RawDescription { get; set; }
    public string NormalizedSignature { get; set; } = string.Empty;
    public decimal? RawAmount { get; set; }
    public DateOnly? RawDate { get; set; }

    /// <summary>True when the line came from free text (PDF) and amount/date were not structurally extracted.</summary>
    public bool IsFreeText { get; set; }

    public string? MerchantName { get; set; }
    public decimal? Amount { get; set; }
    public string Currency { get; set; } = "BRL";
    public DateOnly? TransactionDate { get; set; }
    public TransactionType? TransactionType { get; set; }
    public string? Category { get; set; }
    public string? Subcategory { get; set; }
    public PaymentMethod? PaymentMethod { get; set; }
    public int? InstallmentCurrent { get; set; }
    public int? InstallmentTotal { get; set; }
    public bool IsRecurring { get; set; }
    public decimal ConfidenceScore { get; set; }
    public bool NeedsReview { get; set; }
    public ResolutionSource? ResolutionSource { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
}
