using CsvHelper;
using Mycroft.Core.Enums;
using Mycroft.Core.Interfaces;
using Mycroft.Core.Models;
using Mycroft.Core.Services;

namespace Mycroft.Infrastructure.Parsing;

/// <summary>
/// Header-driven CSV parser. Detects the delimiter (; , tab |) and maps common pt-BR/en column names.
/// Required: a description column and an amount column. Optional: date, id and debit/credit type.
/// </summary>
public sealed class CsvStatementParser : IStatementParser
{
    public SourceFormat Format => SourceFormat.Csv;

    private static readonly string[] DateHeaders = ["DATA", "DATE", "DT", "DATALANCAMENTO", "DATADACOMPRA", "DATAMOVIMENTO", "DATAMOVIMENTACAO", "TRANSACTIONDATE", "RAWDATE"];
    private static readonly string[] DescriptionHeaders = ["DESCRICAO", "DESCRIPTION", "HISTORICO", "LANCAMENTO", "MEMO", "ESTABELECIMENTO", "TITULO", "TITLE", "RAWDESCRIPTION", "DETALHES", "NOME"];
    private static readonly string[] AmountHeaders = ["VALOR", "AMOUNT", "VALUE", "VALORRS", "QUANTIA", "RAWAMOUNT", "VALORBRL"];
    private static readonly string[] IdHeaders = ["ID", "IDENTIFICADOR", "FITID", "EXTERNALLINEID", "DOCUMENTO", "NUMERODOCUMENTO"];
    private static readonly string[] TypeHeaders = ["TIPO", "TYPE", "DC", "NATUREZA"];

    public IReadOnlyList<RawStatementLine> Parse(ReadOnlyMemory<byte> content)
    {
        var text = ParsingHelpers.Decode(content.Span);
        var firstLine = ParsingHelpers.SplitLines(text).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))
                        ?? throw new StatementParseException("CSV file is empty.");

        var options = new CsvReaderOptions
        {
            Separator = DetectDelimiter(firstLine),
            HasHeader = true,
            TrimWhitespace = true,
        };

        using var csv = CsvReader.FromText(text, options);
        var hasRecord = csv.Read(); // reads the header and the first data record
        if (csv.Headers.Count == 0)
            throw new StatementParseException("CSV file has no header row.");

        var headers = csv.Headers.Select(h => TextNormalization.ToUpperAsciiTokens(h).Replace(" ", "")).ToArray();
        int Find(string[] names) => Array.FindIndex(headers, h => names.Contains(h));

        var descIdx = Find(DescriptionHeaders);
        var amountIdx = Find(AmountHeaders);
        if (descIdx < 0 || amountIdx < 0)
            throw new StatementParseException(
                "CSV header must contain a description column (e.g. 'descricao', 'historico', 'description') and an amount column (e.g. 'valor', 'amount').");

        var dateIdx = Find(DateHeaders);
        var idIdx = Find(IdHeaders);
        var typeIdx = Find(TypeHeaders);

        var lines = new List<RawStatementLine>();
        for (; hasRecord; hasRecord = csv.Read())
        {
            string? Get(int idx) => idx >= 0 && idx < csv.FieldCount ? csv.GetString(idx) : null;

            var description = Get(descIdx)?.Trim();
            if (string.IsNullOrWhiteSpace(description)) continue;

            var amount = ParsingHelpers.ParseAmount(Get(amountIdx));
            if (amount is not null && typeIdx >= 0)
            {
                var type = TextNormalization.ToUpperAsciiTokens(Get(typeIdx) ?? string.Empty);
                if (type.StartsWith('D')) amount = -Math.Abs(amount.Value);
                else if (type.StartsWith('C')) amount = Math.Abs(amount.Value);
            }

            lines.Add(new RawStatementLine(
                LineIndex: lines.Count,
                RawDescription: description,
                RawAmount: amount,
                RawDate: dateIdx >= 0 ? ParsingHelpers.ParseDate(Get(dateIdx)) : null,
                ExternalLineId: idIdx >= 0 ? NullIfEmpty(Get(idIdx)) : null));
        }

        return lines;
    }

    private static char DetectDelimiter(string headerLine)
    {
        char[] candidates = [';', ',', '\t', '|'];
        return candidates.MaxBy(c => headerLine.Count(ch => ch == c));
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
