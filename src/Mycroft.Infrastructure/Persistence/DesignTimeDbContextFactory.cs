using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Mycroft.Infrastructure.Persistence;

/// <summary>Used only by `dotnet ef` to create migrations; no database connection is opened.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MycroftDbContext>
{
    public MycroftDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MycroftDbContext>()
            .UseNpgsql("Host=localhost;Database=mycroft;Username=mycroft;Password=mycroft")
            .Options;
        return new MycroftDbContext(options);
    }
}
