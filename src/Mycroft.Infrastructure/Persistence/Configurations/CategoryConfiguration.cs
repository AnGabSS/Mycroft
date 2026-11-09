using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Mycroft.Core.Entities;

namespace Mycroft.Infrastructure.Persistence.Configurations;

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> b)
    {
        b.ToTable("Categories");
        b.HasKey(c => c.Id);
        b.Property(c => c.Name).HasMaxLength(100).IsRequired();
        b.HasIndex(c => c.Name).IsUnique();
    }
}
