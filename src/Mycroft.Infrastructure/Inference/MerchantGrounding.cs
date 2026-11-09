using Mycroft.Core.Services;

namespace Mycroft.Infrastructure.Inference;

/// <summary>
/// Checks that a merchant name proposed by the model is related to the transaction's own description.
/// Small models leak merchants between neighbouring lines ("PAGTO SALARIOS" → "Loja IFD I", "PAY LAN H" → "Uber");
/// those answers cannot come from the text and are replaced by the cleaned description.
/// A merchant token is grounded when it appears in the description, shares a prefix with one of its tokens
/// ("FERNAND" → "Fernanda", "TECNOLOG" → "Tecnologia"), or one of its tokens is an abbreviation of it
/// ("IFD" → "iFood": same first letter, letters in order).
/// </summary>
public static class MerchantGrounding
{
    /// <summary>Filler words models add on their own; ignored unless they are in the description.</summary>
    private static readonly HashSet<string> GenericWords =
        ["LOJA", "EXEMPLO", "ESTABELECIMENTO", "COMERCIO", "EMPRESA", "DE", "DO", "DA", "DOS", "DAS", "E"];

    public static bool IsGrounded(string merchantName, string rawDescription, string normalizedSignature)
    {
        var descriptionTokens = TextNormalization.ToUpperAsciiTokens(rawDescription).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Concat(normalizedSignature.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(t => t.Length >= 3)
            .Distinct()
            .ToList();
        var compactDescription = TextNormalization.ToUpperAsciiTokens(rawDescription).Replace(" ", "");

        var merchantTokens = TextNormalization.ToUpperAsciiTokens(merchantName).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 2 && (!GenericWords.Contains(t) || descriptionTokens.Contains(t)))
            .ToList();
        if (merchantTokens.Count == 0) return false;

        return merchantTokens.Any(m =>
            (m.Length >= 3 && compactDescription.Contains(m, StringComparison.Ordinal))
            || (m.Length < 3 && descriptionTokens.Contains(m))
            || descriptionTokens.Any(d => m.StartsWith(d, StringComparison.Ordinal)
                                         || (m.Length >= 3 && d.StartsWith(m, StringComparison.Ordinal))
                                         || IsAbbreviationOf(d, m)));
    }

    private static readonly HashSet<string> InventedPrefixes = ["LOJA", "EXEMPLO", "ESTABELECIMENTO", "COMERCIO", "EMPRESA"];

    /// <summary>Removes leading filler words the model added on its own ("Loja Bravo" → "Bravo" when "LOJA" is not in the text).</summary>
    public static string StripInventedPrefixes(string merchantName, string rawDescription)
    {
        var description = $" {TextNormalization.ToUpperAsciiTokens(rawDescription)} ";
        var words = merchantName.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1)
        {
            var upper = TextNormalization.ToUpperAsciiTokens(words[0]);
            if (!InventedPrefixes.Contains(upper) || description.Contains($" {upper} ", StringComparison.Ordinal)) break;
            words.RemoveAt(0);
        }
        return string.Join(' ', words);
    }

    /// <summary>"IFD" abbreviates "IFOOD": same first letter and all letters appear in order.</summary>
    private static bool IsAbbreviationOf(string shortForm, string longForm)
    {
        if (shortForm.Length < 3 || shortForm.Length >= longForm.Length || shortForm[0] != longForm[0]) return false;
        var j = 0;
        foreach (var c in longForm)
        {
            if (c == shortForm[j] && ++j == shortForm.Length) return true;
        }
        return false;
    }
}
