using System.Text.RegularExpressions;
using Mycroft.Core.Enums;
using Mycroft.Core.Interfaces;
using Mycroft.Core.Models;
using Mycroft.Core.Services;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Mycroft.Infrastructure.Parsing;

/// <summary>
/// Extracts native text from digital PDFs (statements / invoices) with PdfPig.
/// <list type="bullet">
/// <item>Lines shaped like <c>dd/mm/yyyy DESCRIPTION -1.234,56 [balance]</c> (the layout used by most Brazilian bank
/// statements) become <b>structured</b> lines: date and amount are extracted deterministically, so aliases and the
/// cache can resolve them without the SLM.</item>
/// <item>Other lines containing a money value become free-text lines; the SLM extracts amount/date.</item>
/// <item>Balance/total rows and rows without any description (summary boxes) are skipped.</item>
/// </list>
/// Sign convention: if any structured line carries a minus sign the document is signed (bank statement:
/// negative = debit). Otherwise (card invoices list purchases unsigned) amounts are debits unless the
/// description indicates a credit (payment, refund...).
/// </summary>
public sealed partial class PdfStatementParser : IStatementParser
{
    /// <summary>Descriptions starting with these words are balances/totals, not transactions (compared accent-free, uppercase).</summary>
    private static readonly string[] IgnoredDescriptionPrefixes =
    [
        "SALDO", "TOTAL", "SUBTOTAL", "LIMITE", "VENCIMENTO", "PAGAMENTO MINIMO", "VALOR TOTAL", "ENCARGOS",
        "S A L D O", "LANCAMENTOS FUTUROS",
    ];

    private static readonly string[] UnsignedCreditKeywords =
        ["PAGAMENTO EFETUADO", "PAGAMENTO RECEBIDO", "PAGTO RECEBIDO", "ESTORNO", "CREDITO", "REEMBOLSO", "DEVOLUCAO", "CASHBACK"];

    private const string Money = @"-?\d{1,3}(?:\.\d{3})*,\d{2}";

    /// <summary>
    /// Real statement lines are well under 200 characters. Longer "lines" are not transactions and, since PDF text
    /// is attacker-controlled, they are dropped before any regex runs (defense against regex CPU exhaustion).
    /// </summary>
    internal const int MaxLineLength = 500;

    /// <summary>
    /// dd/mm[/yyyy] DESCRIPTION amount [balance]. NonBacktracking guarantees linear time on hostile input
    /// (the backtracking version took 30s on a crafted 56 KB line).
    /// </summary>
    private static readonly Regex StructuredLine = new(
        @"^(?<date>\d{2}/\d{2}(?:/\d{2,4})?)\s+(?<desc>.*?\p{L}.*?)\s+(?:R\$\s*)?(?<amount>" + Money + @")(?:\s+(?:R\$\s*)?" + Money + @")?\s*-?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    [GeneratedRegex(@"^\d{2}/\d{2}(?:/\d{2,4})?\s+", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingDateRegex();

    public SourceFormat Format => SourceFormat.Pdf;

    public IReadOnlyList<RawStatementLine> Parse(ReadOnlyMemory<byte> content)
    {
        PdfDocument document;
        try
        {
            document = PdfDocument.Open(content.ToArray());
        }
        catch (Exception ex)
        {
            throw new StatementParseException($"Invalid PDF file: {ex.Message}", ex);
        }

        var candidates = new List<Candidate>();
        using (document)
        {
            var anyText = false;
            foreach (var page in document.GetPages())
            {
                var text = ContentOrderTextExtractor.GetText(page);
                if (!string.IsNullOrWhiteSpace(text)) anyText = true;

                foreach (var raw in ParsingHelpers.SplitLines(text))
                {
                    var candidate = Classify(CollapseWhitespace(raw));
                    if (candidate is not null) candidates.Add(candidate);
                }
            }

            if (!anyText) throw new UnreadablePdfTextException();
        }

        var signed = candidates.Any(c => c.Amount is < 0);
        return candidates.Select((c, i) => c.ToLine(i, signed)).ToList();
    }

    private sealed record Candidate(string Description, decimal? Amount, DateOnly? Date, bool Structured)
    {
        public RawStatementLine ToLine(int index, bool signedDocument)
        {
            if (!Structured) return new RawStatementLine(index, Description, IsFreeText: true);

            var amount = Amount!.Value;
            if (!signedDocument)
            {
                var upper = TextNormalization.ToUpperAsciiTokens(Description);
                amount = UnsignedCreditKeywords.Any(k => upper.Contains(k, StringComparison.Ordinal)) ? Math.Abs(amount) : -Math.Abs(amount);
            }

            return new RawStatementLine(index, Description, amount, Date);
        }
    }

    private static Candidate? Classify(string line)
    {
        if (line.Length is < 4 or > MaxLineLength || !ParsingHelpers.MoneyRegex().IsMatch(line)) return null;

        var description = LeadingDateRegex().Replace(line, string.Empty);
        var upperDescription = TextNormalization.ToUpperAsciiTokens(description);

        // Rows like "R$ -100,00 R$ 100,00 R$ 50,00" (summary boxes) have no real description.
        if (!upperDescription.Replace("R ", " ").Any(char.IsLetter)) return null;
        if (IgnoredDescriptionPrefixes.Any(p => upperDescription.StartsWith(p, StringComparison.Ordinal))) return null;

        var m = StructuredLine.Match(line);
        if (m.Success)
        {
            // "dd/mm" without year (common in card invoices) leaves the date to the SLM; the amount is still structural.
            var date = ParsingHelpers.ParseDate(m.Groups["date"].Value);
            var amount = ParsingHelpers.ParseAmount(m.Groups["amount"].Value);
            if (amount is not null)
                return new Candidate(m.Groups["desc"].Value.Trim(), amount, date, Structured: true);
        }

        return new Candidate(line, null, null, Structured: false);
    }

    private static string CollapseWhitespace(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
