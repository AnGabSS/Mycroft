using System.Text.RegularExpressions;
using Mycroft.Core.Interfaces;
using Mycroft.Core.Services;

namespace Mycroft.Infrastructure.Inference;

/// <summary>
/// Builds a stable signature for cache lookups: "PGTO*IFOOD*SAOPAULO*BR 45,90" → "PGTO IFOOD SAOPAULO BR".
/// Removes dates, money values, installment markers, numeric tokens and long alphanumeric codes.
/// </summary>
public sealed partial class MerchantNormalizer : IMerchantNormalizer
{
    public const int MaxLength = 300;

    [GeneratedRegex(@"\bPARC(?:ELA)?\.?\s*\d{1,2}\s*(?:/|DE)\s*\d{1,2}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstallmentRegex();

    // No leading \b on purpose: bank statements glue the purchase date to truncated names ("FERNAND06/11", "S.01/11").
    [GeneratedRegex(@"(?<!\d)\d{1,2}/\d{1,2}(?:/\d{2,4})?(?!\d)|\b\d{4}-\d{2}-\d{2}\b|\b\d{1,2}[.-]\d{1,2}[.-]\d{2,4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex DateLikeRegex();

    [GeneratedRegex(@"(?:R\$\s*)?-?\d{1,3}(?:\.\d{3})*,\d{2}\b|\b\d+\.\d{2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex MoneyRegex();

    [GeneratedRegex(@"\d", RegexOptions.CultureInvariant)]
    private static partial Regex DigitRegex();

    public string Normalize(string rawDescription)
    {
        if (string.IsNullOrWhiteSpace(rawDescription)) return string.Empty;

        var s = InstallmentRegex().Replace(rawDescription, " ");
        s = MoneyRegex().Replace(s, " ");
        s = DateLikeRegex().Replace(s, " ");
        s = TextNormalization.ToUpperAsciiTokens(s);

        var tokens = s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !IsVariableToken(t))
            .ToList();

        var signature = string.Join(' ', tokens);
        if (signature.Length == 0) signature = s; // never return empty when there was content
        return signature.Length > MaxLength ? signature[..MaxLength].TrimEnd() : signature;
    }

    // Pure numbers (auth codes, NSU, terminal ids) and codes with 4+ digits mixed in letters vary per purchase.
    // Short alphanumerics like "99POP" or "4G" are kept because they are often part of the merchant name.
    private static bool IsVariableToken(string token)
    {
        var digits = DigitRegex().Count(token);
        if (digits == 0) return false;
        if (digits == token.Length) return true;
        return digits >= 4;
    }
}
