using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Mycroft.Core.Entities;

namespace Mycroft.Infrastructure.Persistence.Configurations;

internal sealed class MerchantPatternConfiguration : IEntityTypeConfiguration<MerchantPattern>
{
    public void Configure(EntityTypeBuilder<MerchantPattern> b)
    {
        b.ToTable("MerchantPatterns");
        b.HasKey(p => p.NormalizedSignature);
        b.Property(p => p.NormalizedSignature).HasMaxLength(300);
        b.Property(p => p.MerchantName).HasMaxLength(200).IsRequired();
        b.Property(p => p.Category).HasMaxLength(100);
        b.Property(p => p.Subcategory).HasMaxLength(100);
        b.Property(p => p.DefaultPaymentMethod).HasMaxLength(30);
        b.Property(p => p.ConfidenceScore).HasPrecision(4, 3);
    }
}
