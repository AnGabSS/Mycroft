using Mycroft.Core.Enums;

namespace Mycroft.Core.Entities;

/// <summary>Inbox record for a batch of transactions to reconcile.</summary>
public sealed class ReconciliationJob
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string? ExternalReferenceId { get; set; }
    public required string PayloadHash { get; set; }
    public SourceFormat SourceFormat { get; set; }
    public string? OriginalFileName { get; set; }
    public byte[]? RawFilePayload { get; set; }
    public string? CustomCategoriesJson { get; set; }
    public string? CallbackUrl { get; set; }
    public string? WebhookSecret { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Pending;

    public int TotalItems { get; set; }
    public int ProcessedItems { get; set; }
    public int AliasHits { get; set; }
    public int CacheHits { get; set; }
    public int FallbackItems { get; set; }

    public WebhookStatus WebhookStatus { get; set; } = WebhookStatus.NotConfigured;
    public int WebhookAttempts { get; set; }
    public DateTimeOffset? NextWebhookRetryAt { get; set; }

    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public List<TransactionItem> Items { get; set; } = [];

    public bool IsFinished => Status is JobStatus.Completed or JobStatus.CompletedWithWarnings or JobStatus.Failed;
}
