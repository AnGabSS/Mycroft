using Mycroft.Core.Models;

namespace Mycroft.Core.Services;

/// <summary>Result of a successful alias match.</summary>
public sealed record AliasMatch(string CategoryName, CategoryAlias Alias, string CleanPattern);

/// <summary>
/// Matches normalized signatures against job-scoped aliases using SQL LIKE-style wildcards.
/// Precedence: the alias whose pattern (without wildcards) is longest wins ("longest match wins");
/// ties keep the order in which the client sent them.
/// </summary>
public sealed class AliasMatcher
{
    private enum MatchKind { Exact, StartsWith, EndsWith, Contains }

    private sealed record CompiledAlias(string CategoryName, CategoryAlias Alias, string Clean, MatchKind Kind);

    private readonly IReadOnlyList<CompiledAlias> _aliases;

    public AliasMatcher(IEnumerable<CustomCategory> categories)
    {
        _aliases = categories
            .SelectMany(c => c.Aliases.Select(a => Compile(c.Name, a)))
            .Where(a => a is not null)
            .Select(a => a!)
            .OrderByDescending(a => a.Clean.Length) // OrderBy is stable: ties keep input order
            .ToList();
    }

    public bool IsEmpty => _aliases.Count == 0;

    public AliasMatch? Match(string normalizedSignature)
    {
        if (string.IsNullOrEmpty(normalizedSignature)) return null;

        foreach (var alias in _aliases)
        {
            if (IsMatch(normalizedSignature, alias))
                return new AliasMatch(alias.CategoryName, alias.Alias, alias.Clean);
        }

        return null;
    }

    private static CompiledAlias? Compile(string category, CategoryAlias alias)
    {
        var raw = alias.Pattern?.Trim() ?? string.Empty;
        if (raw.Length == 0) return null;

        var leading = raw.StartsWith('%');
        var trailing = raw.EndsWith('%');
        var clean = TextNormalization.ToUpperAsciiTokens(raw.Replace('%', ' '));
        if (clean.Length == 0) return null;

        var kind = (leading, trailing) switch
        {
            (true, true) => MatchKind.Contains,
            (false, true) => MatchKind.StartsWith,
            (true, false) => MatchKind.EndsWith,
            _ => MatchKind.Exact
        };

        return new CompiledAlias(category, alias, clean, kind);
    }

    // Matching is evaluated on the whole signature and at token boundaries, so "IFOOD%" matches
    // "PGTO IFOOD SAOPAULO BR" (token starts with IFOOD) and "%PIPIPOPO" matches "COMPRA EUPIPIPOPO 1".
    private static bool IsMatch(string signature, CompiledAlias alias)
    {
        var padded = $" {signature} ";
        return alias.Kind switch
        {
            MatchKind.Exact => padded.Contains($" {alias.Clean} ", StringComparison.Ordinal),
            MatchKind.StartsWith => padded.Contains($" {alias.Clean}", StringComparison.Ordinal),
            MatchKind.EndsWith => padded.Contains($"{alias.Clean} ", StringComparison.Ordinal),
            MatchKind.Contains => signature.Contains(alias.Clean, StringComparison.Ordinal),
            _ => false
        };
    }
}
