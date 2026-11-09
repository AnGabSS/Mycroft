namespace Mycroft.Core.Models;

/// <summary>A category supplied by the client for a single job, with optional aliases.</summary>
public sealed record CustomCategory
{
    public required string Name { get; init; }
    public IReadOnlyList<string> Subcategories { get; init; } = [];
    public IReadOnlyList<CategoryAlias> Aliases { get; init; } = [];
}

/// <summary>
/// A deterministic mapping from a description pattern to a merchant. Supports SQL LIKE-style
/// wildcards: <c>ABC</c> (exact/token), <c>ABC%</c> (starts with), <c>%ABC</c> (ends with), <c>%ABC%</c> (contains).
/// </summary>
public sealed record CategoryAlias
{
    public required string Pattern { get; init; }
    public string? MerchantName { get; init; }
    public string? Subcategory { get; init; }
}

/// <summary>A taxonomy entry offered to the SLM (name + allowed subcategories).</summary>
public sealed record TaxonomyEntry(string Name, IReadOnlyList<string> Subcategories);
