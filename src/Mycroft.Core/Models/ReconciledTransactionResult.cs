using Mycroft.Core.Enums;

namespace Mycroft.Core.Models;

/// <summary>Structured enrichment result for one line.</summary>
public sealed record ReconciledTransactionResult
{
    public int LineIndex { get; init; }
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
    public ResolutionSource Source { get; init; }
}

/// <summary>A line submitted to the inference service, optionally with deterministic hints.</summary>
/// <param name="Line">The raw line.</param>
/// <param name="NormalizedSignature">Normalized signature of the description.</param>
/// <param name="Hint">Optional deterministic hint (alias match on free text, known merchant name).</param>
public sealed record InferenceRequestItem(RawStatementLine Line, string NormalizedSignature, InferenceHint? Hint = null);

public sealed record InferenceHint(string? MerchantName, string? Category, string? Subcategory);

/// <summary>Context shared by all items of an inference call.</summary>
public sealed record InferenceContext(IReadOnlyList<TaxonomyEntry> Taxonomy);
