namespace Mycroft.Core.Models;

/// <summary>A single line extracted from an input (JSON batch or parsed file) before enrichment.</summary>
/// <param name="LineIndex">Zero-based position in the source.</param>
/// <param name="RawDescription">Original description text.</param>
/// <param name="RawAmount">Signed amount when structurally known (negative = debit).</param>
/// <param name="RawDate">Transaction date when structurally known.</param>
/// <param name="ExternalLineId">Client or file-provided identifier (e.g. OFX FITID).</param>
/// <param name="IsFreeText">True when the line is unstructured text (PDF) and amount/date were guessed or missing.</param>
public sealed record RawStatementLine(
    int LineIndex,
    string RawDescription,
    decimal? RawAmount = null,
    DateOnly? RawDate = null,
    string? ExternalLineId = null,
    bool IsFreeText = false);
