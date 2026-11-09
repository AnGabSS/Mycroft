using System.Globalization;
using Mycroft.Core.Enums;
using Mycroft.Core.Interfaces;
using Mycroft.Core.Models;

namespace Mycroft.Infrastructure.Parsing;

/// <summary>
/// FEBRABAN positional parsers.
/// <list type="bullet">
/// <item><b>CNAB 240</b>: bank statement (extrato para conciliação) — detail records type '3', segment 'E'.</item>
/// <item><b>CNAB 400</b>: collection return file (retorno de cobrança) — detail records type '1', only settlement
/// occurrences (liquidação) become transactions. Field positions follow the common Bradesco/Itaú layout.</item>
/// </list>
/// Positions in comments are 1-based as in the FEBRABAN manuals.
/// </summary>
public sealed class CnabStatementParser(SourceFormat format) : IStatementParser
{
    /// <summary>CNAB 400 occurrence codes that represent a settled (paid) title.</summary>
    private static readonly HashSet<string> SettlementOccurrences = ["06", "07", "08", "15", "16", "17"];

    public SourceFormat Format { get; } = format is SourceFormat.Cnab240 or SourceFormat.Cnab400
        ? format
        : throw new ArgumentOutOfRangeException(nameof(format));

    public IReadOnlyList<RawStatementLine> Parse(ReadOnlyMemory<byte> content)
    {
        var text = ParsingHelpers.Decode(content.Span);
        var lines = new List<RawStatementLine>();
        var recordLength = Format == SourceFormat.Cnab240 ? 240 : 400;

        foreach (var rawLine in ParsingHelpers.SplitLines(text))
        {
            if (rawLine.Length == 0) continue;
            if (rawLine.Length < recordLength - 2) // tolerate missing trailing blanks only
                throw new StatementParseException($"Invalid {Format} record: expected {recordLength} positions, got {rawLine.Length}.");

            var record = rawLine.Length >= recordLength ? rawLine.AsSpan(0, recordLength) : rawLine.PadRight(recordLength).AsSpan();
            var parsed = Format == SourceFormat.Cnab240 ? ParseCnab240(record, lines.Count) : ParseCnab400(record, lines.Count);
            if (parsed is not null) lines.Add(parsed);
        }

        return lines;
    }

    private static RawStatementLine? ParseCnab240(ReadOnlySpan<char> r, int index)
    {
        // 008 record type ('3' = detail), 014 segment
        if (r[7] != '3' || r[13] != 'E') return null;

        var date = ParseDdMmYyyy(Field(r, 143, 150));        // 143-150 data do lançamento
        var amount = ParseImpliedDecimal(Field(r, 151, 168));  // 151-168 valor (16,2)
        var dc = Field(r, 169, 169);                           // 169 tipo lançamento D/C
        var historyCode = Field(r, 173, 176).Trim();           // 173-176 código histórico
        var history = Field(r, 177, 201).Trim();               // 177-201 descrição do histórico
        var complement = Field(r, 114, 133).Trim();            // 114-133 complemento
        var document = Field(r, 202, 240).Trim();              // 202-240 número do documento

        if (amount is not null && dc is "D" or "d") amount = -amount;

        var description = string.Join(' ', new[] { history, complement }.Where(s => s.Length > 0));
        if (description.Length == 0) description = $"HISTORICO {historyCode}";

        return new RawStatementLine(index, description, amount, date, NullIfEmpty(document));
    }

    private static RawStatementLine? ParseCnab400(ReadOnlySpan<char> r, int index)
    {
        if (r[0] != '1') return null;                               // 001 detail record

        var occurrence = Field(r, 109, 110);                        // 109-110 código de ocorrência
        if (!SettlementOccurrences.Contains(occurrence)) return null;

        var date = ParseDdMmYy(Field(r, 111, 116));                 // 111-116 data da ocorrência
        var document = Field(r, 117, 126).Trim();                   // 117-126 número do documento
        var faceValue = ParseImpliedDecimal(Field(r, 153, 165));    // 153-165 valor do título
        var paidValue = ParseImpliedDecimal(Field(r, 254, 266));    // 254-266 valor pago
        var payer = Field(r, 325, 354).Trim();                      // 325-354 nome do sacado (layouts que o informam)

        var amount = paidValue is > 0 ? paidValue : faceValue;
        var description = $"LIQUIDACAO BOLETO DOC {document} OCORR {occurrence}" + (IsPrintable(payer) ? $" {payer}" : string.Empty);

        return new RawStatementLine(index, description, amount, date, NullIfEmpty(document));
    }

    private static string Field(ReadOnlySpan<char> record, int from, int to) => record.Slice(from - 1, to - from + 1).ToString();

    private static decimal? ParseImpliedDecimal(string digits)
    {
        var trimmed = digits.Trim();
        if (trimmed.Length == 0 || !long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var cents)) return null;
        return cents / 100m;
    }

    private static DateOnly? ParseDdMmYyyy(string s) =>
        DateOnly.TryParseExact(s, "ddMMyyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static DateOnly? ParseDdMmYy(string s) =>
        DateOnly.TryParseExact(s, "ddMMyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static bool IsPrintable(string s) => s.Length > 0 && s.Any(char.IsLetter);

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) || s.All(c => c == '0') ? null : s;
}
