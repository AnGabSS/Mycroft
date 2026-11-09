using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Mycroft.Core.Entities;

namespace Mycroft.Infrastructure.Persistence.Configurations;

internal sealed class ReconciliationJobConfiguration : IEntityTypeConfiguration<ReconciliationJob>
{
    public void Configure(EntityTypeBuilder<ReconciliationJob> b)
    {
        b.ToTable("ReconciliationJobs");
        b.HasKey(j => j.Id);
        b.Property(j => j.ExternalReferenceId).HasMaxLength(120);
        b.Property(j => j.PayloadHash).HasMaxLength(64).IsFixedLength().IsRequired();
        b.Property(j => j.SourceFormat).HasMaxLength(20);
        b.Property(j => j.OriginalFileName).HasMaxLength(255);
        b.Property(j => j.CallbackUrl).HasMaxLength(500);
        b.Property(j => j.WebhookSecret).HasMaxLength(255);
        b.Property(j => j.Status).HasMaxLength(30);
        b.Property(j => j.WebhookStatus).HasMaxLength(30);
        b.Ignore(j => j.IsFinished);

        b.HasIndex(j => j.ExternalReferenceId).IsUnique().HasFilter("\"ExternalReferenceId\" IS NOT NULL");
        b.HasIndex(j => new { j.PayloadHash, j.CreatedAt });
        b.HasIndex(j => j.Status);
        b.HasIndex(j => new { j.WebhookStatus, j.NextWebhookRetryAt });
        b.HasIndex(j => j.CreatedAt);

        b.HasMany(j => j.Items).WithOne(i => i.Job).HasForeignKey(i => i.JobId).OnDelete(DeleteBehavior.Cascade);
    }
}
