using System.Globalization;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Mycroft.Core.Enums;
using Mycroft.Core.Interfaces;
using Mycroft.Core.Models;

namespace Mycroft.Infrastructure.Parsing;

/// <summary>
/// Lightweight OFX parser supporting OFX 1.x (SGML, unclosed leaf tags) and OFX 2.x (XML).
/// The body is normalized to well-formed XML and every &lt;STMTTRN&gt; becomes a line.
/// </summary>
public sealed partial class OfxStatementParser : IStatementParser
{
    public SourceFormat Format => SourceFormat.Ofx;

    public IReadOnlyList<RawStatementLine> Parse(ReadOnlyMemory<byte> content)
    {
        var text = ParsingHelpers.Decode(content.Span);
        var start = text.IndexOf("<OFX>", StringComparison.OrdinalIgnoreCase);
        if (start < 0) throw new StatementParseException("OFX file does not contain an <OFX> root element.");

        XDocument doc;
        try
        {
            var xml = SgmlToXml(text[start..]);
            doc = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new StatementParseException($"Invalid OFX structure: {ex.Message}", ex);
        }

        var lines = new List<RawStatementLine>();
        foreach (var trn in doc.Descendants("STMTTRN"))
        {
            var name = Value(trn, "NAME");
            var memo = Value(trn, "MEMO");
            var description = string.Join(' ', new[] { name, memo }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(description)) description = Value(trn, "TRNTYPE") ?? "SEM DESCRICAO";

            var amount = ParseOfxAmount(Value(trn, "TRNAMT"));
            var date = ParseOfxDate(Value(trn, "DTPOSTED"));

            lines.Add(new RawStatementLine(
                LineIndex: lines.Count,
                RawDescription: description.Trim(),
                RawAmount: amount,
                RawDate: date,
                ExternalLineId: Value(trn, "FITID")));
        }

        return lines;
    }

    private static string? Value(XElement parent, string name)
    {
        var v = parent.Element(name)?.Value.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    private static decimal? ParseOfxAmount(string? raw)
    {
        if (raw is null) return null;
        // OFX uses '.' as decimal separator, but some Brazilian banks emit ','.
        var s = raw.Replace(',', '.');
        return decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v)
            ? v
            : ParsingHelpers.ParseAmount(raw);
    }

    private static DateOnly? ParseOfxDate(string? raw)
    {
        if (raw is null || raw.Length < 8) return null;
        return DateOnly.TryParseExact(raw[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    [GeneratedRegex(@"<(?<close>/?)(?<name>[A-Za-z0-9_.]+)>(?<text>[^<]*)", RegexOptions.CultureInvariant)]
    private static partial Regex TagRegex();

    /// <summary>
    /// Converts OFX SGML into XML: a tag followed by non-empty text is a leaf and gets closed immediately;
    /// a tag followed by no text is an aggregate. Explicit closing tags for leaves (OFX 2.x) are skipped.
    /// </summary>
    internal static string SgmlToXml(string ofx)
    {
        var sb = new StringBuilder(ofx.Length + 1024);
        var stack = new Stack<string>();
        string? lastLeaf = null;

        foreach (Match m in TagRegex().Matches(ofx))
        {
            var name = m.Groups["name"].Value.ToUpperInvariant();
            var isClose = m.Groups["close"].Value == "/";
            var text = m.Groups["text"].Value.Trim();

            if (isClose)
            {
                if (lastLeaf == name) { lastLeaf = null; continue; } // explicit close of a leaf already closed
                lastLeaf = null;
                if (!stack.Contains(name)) continue;                  // stray close tag
                while (stack.Count > 0)
                {
                    var open = stack.Pop();
                    sb.Append("</").Append(open).Append('>');
                    if (open == name) break;
                }
                continue;
            }

            if (text.Length > 0)
            {
                sb.Append('<').Append(name).Append('>').Append(SecurityElement.Escape(text)).Append("</").Append(name).Append('>');
                lastLeaf = name;
            }
            else
            {
                sb.Append('<').Append(name).Append('>');
                stack.Push(name);
                lastLeaf = null;
            }
        }

        while (stack.Count > 0) sb.Append("</").Append(stack.Pop()).Append('>');
        return sb.ToString();
    }
}
