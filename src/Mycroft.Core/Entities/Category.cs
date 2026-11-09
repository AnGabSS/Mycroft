namespace Mycroft.Core.Entities;

/// <summary>Default (global) taxonomy category. Job-specific categories travel inside the job payload instead.</summary>
public sealed class Category
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public List<string> Subcategories { get; set; } = [];
    public bool IsSystem { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
