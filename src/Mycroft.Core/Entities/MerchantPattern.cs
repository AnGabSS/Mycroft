using Mycroft.Core.Enums;

namespace Mycroft.Core.Entities;

/// <summary>
/// Learned knowledge about a normalized merchant signature. When <see cref="Category"/> is null the
/// pattern only carries merchant metadata (learned from a tenant-private category) and is used as a hint.
/// </summary>
public sealed class MerchantPattern
{
    public const decimal MinCacheConfidence = 0.900m;

    public required string NormalizedSignature { get; set; }
    public required string MerchantName { get; set; }
    public string? Category { get; set; }
    public string? Subcategory { get; set; }
    public PaymentMethod? DefaultPaymentMethod { get; set; }
    public bool IsRecurring { get; set; }
    public decimal ConfidenceScore { get; set; }
    public long HitCount { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
