using Mycroft.Core.Enums;
using Mycroft.Core.Models;

namespace Mycroft.Api.Contracts;

public sealed record BatchReconciliationRequest
{
    /// <summary>Optional client correlation id. Also used for idempotency.</summary>
    public string? ExternalReferenceId { get; init; }
    /// <summary>Webhook URL notified when the job finishes.</summary>
    public string? CallbackUrl { get; init; }
    /// <summary>Secret used to sign the webhook (HMAC-SHA256). Falls back to WEBHOOK_DEFAULT_SECRET.</summary>
    public string? WebhookSecret { get; init; }
    /// <summary>Job-scoped taxonomy with aliases. When omitted the default taxonomy is used.</summary>
    public IReadOnlyList<CustomCategory>? CustomCategories { get; init; }
    public IReadOnlyList<BatchTransaction> Transactions { get; init; } = [];
}

public sealed record BatchTransaction
{
    public string? ExternalLineId { get; init; }
    public string RawDescription { get; init; } = string.Empty;
    /// <summary>Signed amount (negative = debit).</summary>
    public decimal? RawAmount { get; init; }
    public DateOnly? RawDate { get; init; }
}

public sealed record JobAcceptedResponse(Guid JobId, JobStatus Status, string StatusUrl, bool Duplicate);

public sealed record FeedbackRequest
{
    public string? MerchantName { get; init; }
    public string? Category { get; init; }
    public string? Subcategory { get; init; }
    public PaymentMethod? PaymentMethod { get; init; }
    public bool? IsRecurring { get; init; }
}

public sealed record FeedbackResponse(TransactionView Transaction, bool PromotedToGlobalCache);

public sealed record CategoryRequest
{
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<string> Subcategories { get; init; } = [];
}

public sealed record CategoryResponse(Guid Id, string Name, IReadOnlyList<string> Subcategories, bool IsSystem);
