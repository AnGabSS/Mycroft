using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Mycroft.Infrastructure.Parsing;

internal static partial class ParsingHelpers
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>Decodes text files: UTF-8 (with or without BOM), falling back to Latin-1 (typical of bank exports).</summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return Encoding.UTF8.GetString(bytes[3..]);
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    public static string[] SplitLines(string text) =>
        text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);

    /// <summary>
    /// Parses amounts written in pt-BR ("1.234,56"), invariant ("1234.56"), with optional sign,
    /// currency symbol, parentheses or D/C suffix. Returns null when it cannot parse.
    /// </summary>
    public static decimal? ParseAmount(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim()
            .Replace("R$", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" ", "")
            .Replace(" ", "");

        var negative = false;
        if (s.Length > 1 && s[0] == '(' && s[^1] == ')') { negative = true; s = s[1..^1]; }
        if (s.EndsWith('D') || s.EndsWith('d')) { negative = true; s = s[..^1]; }
        else if (s.EndsWith('C') || s.EndsWith('c')) { s = s[..^1]; }
        if (s.EndsWith('-')) { negative = true; s = s[..^1]; }
        if (s.StartsWith('-')) { negative = true; s = s[1..]; }
        else if (s.StartsWith('+')) { s = s[1..]; }

        if (s.Length == 0) return null;

        var lastComma = s.LastIndexOf(',');
        var lastDot = s.LastIndexOf('.');
        string canonical;
        if (lastComma > lastDot)
            canonical = s.Replace(".", "").Replace(',', '.');          // 1.234,56 / 45,90
        else if (lastComma >= 0)
            canonical = s.Replace(",", "");                              // 1,234.56
        else if (s.Count(c => c == '.') > 1)
            canonical = s.Replace(".", "");                              // 1.234.567 (thousands only)
        else
            canonical = s;

        if (!decimal.TryParse(canonical, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            return null;

        return negative ? -value : value;
    }

    private static readonly string[] DateFormats =
        ["dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd", "dd/MM/yy", "dd-MM-yyyy", "dd.MM.yyyy", "yyyyMMdd", "yyyy/MM/dd"];

    public static DateOnly? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        // Drop a time component if present (e.g. "2026-10-02T10:00:00" or "02/10/2026 10:00").
        var cut = s.IndexOfAny(['T', ' ']);
        if (cut > 0) s = s[..cut];

        return DateOnly.TryParseExact(s, DateFormats, PtBr, DateTimeStyles.None, out var d) ? d : null;
    }

    /// <summary>Money pattern like "1.234,56", "-45,90" or "45.90" (two decimal places).</summary>
    [GeneratedRegex(@"(?<![\d,.])-?\d{1,3}(?:\.\d{3})*,\d{2}(?![\d,])|(?<![\d,.])-?\d+\.\d{2}(?![\d.])", RegexOptions.CultureInvariant)]
    public static partial Regex MoneyRegex();

    /// <summary>Dates like 02/10, 02/10/26 or 02/10/2026.</summary>
    [GeneratedRegex(@"(?<![\d/])(?<d>[0-3]?\d)/(?<m>[01]?\d)(?:/(?<y>\d{4}|\d{2}))?(?![\d/])", RegexOptions.CultureInvariant)]
    public static partial Regex ShortDateRegex();
}
