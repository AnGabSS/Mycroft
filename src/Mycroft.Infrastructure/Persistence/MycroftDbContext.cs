using Microsoft.EntityFrameworkCore;
using Mycroft.Core.Entities;

namespace Mycroft.Infrastructure.Persistence;

public sealed class MycroftDbContext(DbContextOptions<MycroftDbContext> options) : DbContext(options)
{
    public DbSet<ReconciliationJob> ReconciliationJobs => Set<ReconciliationJob>();
    public DbSet<TransactionItem> TransactionItems => Set<TransactionItem>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<MerchantPattern> MerchantPatterns => Set<MerchantPattern>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MycroftDbContext).Assembly);

        if (Database.IsNpgsql())
        {
            modelBuilder.Entity<ReconciliationJob>().Property(j => j.CustomCategoriesJson).HasColumnType("jsonb");
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Enums are persisted as readable strings (VARCHAR) as per spec.
        configurationBuilder.Properties<Enum>().HaveConversion<string>();
    }
}
