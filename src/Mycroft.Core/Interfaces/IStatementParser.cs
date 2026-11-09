using Mycroft.Core.Enums;
using Mycroft.Core.Models;

namespace Mycroft.Core.Interfaces;

public interface IStatementParser
{
    SourceFormat Format { get; }
    IReadOnlyList<RawStatementLine> Parse(ReadOnlyMemory<byte> content);
}

public interface IStatementParserFactory
{
    IStatementParser Get(SourceFormat format);

    /// <summary>Best-effort format detection from file name and content.</summary>
    SourceFormat? Detect(string? fileName, ReadOnlySpan<byte> content);
}

/// <summary>Thrown when a file cannot be parsed. The message is safe to expose to clients.</summary>
public class StatementParseException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Thrown for PDFs without an extractable text layer (scanned images).</summary>
public sealed class UnreadablePdfTextException()
    : StatementParseException("UnreadablePdfText: the PDF has no extractable text layer (scanned image PDFs are not supported in v1).");
