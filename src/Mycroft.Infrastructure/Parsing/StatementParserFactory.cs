using Mycroft.Core.Enums;
using Mycroft.Core.Interfaces;

namespace Mycroft.Infrastructure.Parsing;

public sealed class StatementParserFactory(IEnumerable<IStatementParser> parsers) : IStatementParserFactory
{
    private readonly Dictionary<SourceFormat, IStatementParser> _parsers = parsers.ToDictionary(p => p.Format);

    public IStatementParser Get(SourceFormat format) =>
        _parsers.TryGetValue(format, out var parser)
            ? parser
            : throw new StatementParseException($"No parser registered for format '{format}'.");

    public SourceFormat? Detect(string? fileName, ReadOnlySpan<byte> content)
    {
        if (content.StartsWith("%PDF"u8)) return SourceFormat.Pdf;

        var head = ParsingHelpers.Decode(content[..Math.Min(content.Length, 4096)]);
        if (head.Contains("<OFX>", StringComparison.OrdinalIgnoreCase) || head.Contains("OFXHEADER", StringComparison.OrdinalIgnoreCase))
            return SourceFormat.Ofx;

        var firstLine = ParsingHelpers.SplitLines(head).FirstOrDefault() ?? string.Empty;
        // Positional files: the header record has a fixed length. Allow a small tolerance for trimmed blanks.
        if (firstLine.Length is >= 238 and <= 242 && firstLine.Length >= 8 && firstLine[7] == '0') return SourceFormat.Cnab240;
        if (firstLine.Length is >= 398 and <= 402 && firstLine[0] == '0') return SourceFormat.Cnab400;

        var ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => SourceFormat.Pdf,
            ".ofx" or ".qfx" => SourceFormat.Ofx,
            ".csv" or ".tsv" or ".txt" => SourceFormat.Csv,
            _ => firstLine.IndexOfAny([';', ',', '\t']) >= 0 ? SourceFormat.Csv : null
        };
    }
}
