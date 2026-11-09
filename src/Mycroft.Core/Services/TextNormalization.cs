using System.Globalization;
using System.Text;

namespace Mycroft.Core.Services;

public static class TextNormalization
{
    /// <summary>Uppercases, strips diacritics and replaces every non-alphanumeric char (except <paramref name="keep"/>) with a space, collapsing whitespace.</summary>
    public static string ToUpperAsciiTokens(string input, char? keep = null)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var decomposed = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var lastWasSpace = true;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;

            if (char.IsLetterOrDigit(c) || (keep.HasValue && c == keep.Value))
            {
                sb.Append(char.ToUpperInvariant(c));
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }

        return sb.ToString().TrimEnd();
    }
}
