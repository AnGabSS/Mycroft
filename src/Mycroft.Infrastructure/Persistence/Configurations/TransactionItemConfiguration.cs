using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Mycroft.Core.Entities;

namespace Mycroft.Infrastructure.Persistence.Configurations;

internal sealed class TransactionItemConfiguration : IEntityTypeConfiguration<TransactionItem>
{
    public void Configure(EntityTypeBuilder<TransactionItem> b)
    {
        b.ToTable("TransactionItems");
        b.HasKey(i => i.Id);
        b.Property(i => i.ExternalLineId).HasMaxLength(100);
        b.Property(i => i.RawDescription).IsRequired();
        b.Property(i => i.NormalizedSignature).HasMaxLength(300);
        b.Property(i => i.RawAmount).HasPrecision(18, 2);
        b.Property(i => i.MerchantName).HasMaxLength(200);
        b.Property(i => i.Amount).HasPrecision(18, 2);
        b.Property(i => i.Currency).HasMaxLength(3).IsFixedLength().HasDefaultValue("BRL");
        b.Property(i => i.TransactionType).HasMaxLength(15);
        b.Property(i => i.Category).HasMaxLength(100);
        b.Property(i => i.Subcategory).HasMaxLength(100);
        b.Property(i => i.PaymentMethod).HasMaxLength(30);
        b.Property(i => i.ConfidenceScore).HasPrecision(4, 3);
        b.Property(i => i.ResolutionSource).HasMaxLength(20);

        b.HasIndex(i => new { i.JobId, i.LineIndex });
    }
}
