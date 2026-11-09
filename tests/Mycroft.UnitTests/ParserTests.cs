using System.Text;
using FluentAssertions;
using Mycroft.Core.Enums;
using Mycroft.Core.Interfaces;
using Mycroft.Infrastructure.Parsing;

namespace Mycroft.UnitTests;

public class CsvParserTests
{
    private static ReadOnlyMemory<byte> Bytes(string s, Encoding? e = null) => (e ?? Encoding.UTF8).GetBytes(s);

    [Fact]
    public void Parses_semicolon_ptbr_csv()
    {
        var csv = "Data;Descrição;Valor\n02/10/2026;PGTO*IFOOD*SAOPAULO;-45,90\n03/10/2026;SALARIO ACME;1.234,56\n";

        var lines = new CsvStatementParser().Parse(Bytes(csv));

        lines.Should().HaveCount(2);
        lines[0].Should().BeEquivalentTo(new { LineIndex = 0, RawDescription = "PGTO*IFOOD*SAOPAULO", RawAmount = -45.90m, RawDate = new DateOnly(2026, 10, 2) });
        lines[1].RawAmount.Should().Be(1234.56m);
    }

    [Fact]
    public void Parses_comma_csv_with_quotes_type_column_and_latin1()
    {
        var csv = "id,date,description,amount,type\n" +
                  "a1,2026-10-02,\"PADARIA SÃO JOÃO, CENTRO\",12.50,D\n" +
                  "a2,2026-10-03,ESTORNO,5.00,C\n";

        var lines = new CsvStatementParser().Parse(Bytes(csv, Encoding.Latin1));

        lines.Should().HaveCount(2);
        lines[0].RawDescription.Should().Be("PADARIA SÃO JOÃO, CENTRO");
        lines[0].RawAmount.Should().Be(-12.50m);
        lines[0].ExternalLineId.Should().Be("a1");
        lines[1].RawAmount.Should().Be(5.00m);
    }

    [Fact]
    public void Missing_required_columns_throw() =>
        FluentActions.Invoking(() => new CsvStatementParser().Parse(Bytes("foo;bar\n1;2\n")))
            .Should().Throw<StatementParseException>();
}

public class OfxParserTests
{
    private const string Sgml = """
        OFXHEADER:100
        DATA:OFXSGML
        VERSION:102
        CHARSET:1252

        <OFX>
        <SIGNONMSGSRSV1><SONRS><STATUS><CODE>0<SEVERITY>INFO</STATUS><DTSERVER>20261031</SONRS></SIGNONMSGSRSV1>
        <BANKMSGSRSV1><STMTTRNRS><STMTRS><CURDEF>BRL
        <BANKTRANLIST>
        <DTSTART>20261001<DTEND>20261031
        <STMTTRN>
        <TRNTYPE>DEBIT
        <DTPOSTED>20261002120000[-3:BRT]
        <TRNAMT>-45.90
        <FITID>202610020001
        <MEMO>PGTO*IFOOD*SAOPAULO & CIA
        </STMTTRN>
        <STMTTRN>
        <TRNTYPE>CREDIT
        <DTPOSTED>20261005
        <TRNAMT>1500,00
        <FITID>202610050002
        <NAME>PIX RECEBIDO
        <MEMO>JOAO SILVA
        </STMTTRN>
        </BANKTRANLIST>
        </STMTRS></STMTTRNRS></BANKMSGSRSV1>
        </OFX>
        """;

    [Fact]
    public void Parses_sgml_ofx_1x()
    {
        var lines = new OfxStatementParser().Parse(Encoding.Latin1.GetBytes(Sgml));

        lines.Should().HaveCount(2);
        lines[0].Should().BeEquivalentTo(new { RawDescription = "PGTO*IFOOD*SAOPAULO & CIA", RawAmount = -45.90m, RawDate = new DateOnly(2026, 10, 2), ExternalLineId = "202610020001" });
        lines[1].RawDescription.Should().Be("PIX RECEBIDO JOAO SILVA");
        lines[1].RawAmount.Should().Be(1500.00m);
    }

    [Fact]
    public void Parses_xml_ofx_2x()
    {
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <?OFX OFXHEADER="200" VERSION="211"?>
            <OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><BANKTRANLIST>
            <STMTTRN><TRNTYPE>DEBIT</TRNTYPE><DTPOSTED>20261002</DTPOSTED><TRNAMT>-19.90</TRNAMT><FITID>X1</FITID><NAME>NETFLIX.COM</NAME></STMTTRN>
            </BANKTRANLIST></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>
            """;

        var lines = new OfxStatementParser().Parse(Encoding.UTF8.GetBytes(xml));

        lines.Should().ContainSingle().Which.Should().BeEquivalentTo(new { RawDescription = "NETFLIX.COM", RawAmount = -19.90m, ExternalLineId = "X1" });
    }
}

public class CnabParserTests
{
    /// <summary>Builds a fixed-width record placing values at 1-based positions.</summary>
    internal static string Record(int length, params (int Pos, string Value)[] fields)
    {
        var chars = new string(' ', length).ToCharArray();
        foreach (var (pos, value) in fields) value.CopyTo(0, chars, pos - 1, value.Length);
        return new string(chars);
    }

    [Fact]
    public void Parses_cnab240_segment_e()
    {
        var header = Record(240, (1, "341"), (8, "0"));
        var detail = Record(240, (1, "341"), (8, "3"), (14, "E"), (114, "LOJA CENTRO"), (143, "02102026"),
            (151, "000000000000004590"), (169, "D"), (173, "0123"), (177, "COMPRA CARTAO IFOOD"), (202, "DOC123"));
        var credit = Record(240, (8, "3"), (14, "E"), (143, "05102026"), (151, "000000000000150000"), (169, "C"), (177, "PIX RECEBIDO"));
        var segmentD = Record(240, (8, "3"), (14, "D"));
        var content = string.Join("\r\n", header, detail, credit, segmentD);

        var lines = new CnabStatementParser(SourceFormat.Cnab240).Parse(Encoding.ASCII.GetBytes(content));

        lines.Should().HaveCount(2);
        lines[0].Should().BeEquivalentTo(new { RawDescription = "COMPRA CARTAO IFOOD LOJA CENTRO", RawAmount = -45.90m, RawDate = new DateOnly(2026, 10, 2), ExternalLineId = "DOC123" });
        lines[1].RawAmount.Should().Be(1500.00m);
    }

    [Fact]
    public void Parses_cnab400_settlements_only()
    {
        var header = Record(400, (1, "0"));
        var paid = Record(400, (1, "1"), (109, "06"), (111, "051026"), (117, "NF000123"), (153, "0000000010000"), (254, "0000000009950"));
        var registered = Record(400, (1, "1"), (109, "02"), (111, "051026"), (153, "0000000010000"));
        var trailer = Record(400, (1, "9"));

        var lines = new CnabStatementParser(SourceFormat.Cnab400).Parse(Encoding.ASCII.GetBytes(string.Join("\n", header, paid, registered, trailer)));

        lines.Should().ContainSingle().Which.Should().BeEquivalentTo(new { RawAmount = 99.50m, RawDate = new DateOnly(2026, 10, 5), ExternalLineId = "NF000123" });
    }

    [Fact]
    public void Wrong_record_length_throws() =>
        FluentActions.Invoking(() => new CnabStatementParser(SourceFormat.Cnab240).Parse(Encoding.ASCII.GetBytes("short line")))
            .Should().Throw<StatementParseException>();
}

public class StatementParserFactoryTests
{
    private readonly StatementParserFactory _sut = new([]);

    [Fact] public void Detects_pdf_by_magic() => _sut.Detect("x.bin", "%PDF-1.7"u8).Should().Be(SourceFormat.Pdf);
    [Fact] public void Detects_ofx_by_content() => _sut.Detect("x.txt", "OFXHEADER:100\n<OFX>"u8).Should().Be(SourceFormat.Ofx);
    [Fact] public void Detects_csv_by_extension() => _sut.Detect("x.csv", "a;b\n1;2"u8).Should().Be(SourceFormat.Csv);

    [Fact]
    public void Detects_cnab_by_record_length()
    {
        _sut.Detect("x.ret", Encoding.ASCII.GetBytes(CnabParserTests.Record(240, (8, "0")))).Should().Be(SourceFormat.Cnab240);
        _sut.Detect("x.ret", Encoding.ASCII.GetBytes(CnabParserTests.Record(400, (1, "0")))).Should().Be(SourceFormat.Cnab400);
    }

    [Fact]
    public void Scanned_pdf_without_text_is_reported_as_unreadable()
    {
        // Minimal valid PDF with one empty page (no text layer).
        var pdf = MinimalPdf();
        FluentActions.Invoking(() => new PdfStatementParser().Parse(pdf)).Should().Throw<UnreadablePdfTextException>();
    }

    private static byte[] MinimalPdf()
    {
        using var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        return builder.Build();
    }

    private static byte[] Pdf(params string[] lines)
    {
        using var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        for (var i = 0; i < lines.Length; i++)
            page.AddText(lines[i], 9, new UglyToad.PdfPig.Core.PdfPoint(30, 800 - i * 14), font);
        return builder.Build();
    }

    [Fact]
    public void Bank_statement_pdf_lines_are_structured_and_balances_skipped()
    {
        // Layout of a bank "extrato conta / lancamentos" export (fictitious data).
        var pdf = Pdf(
            "saldo em conta Limite da Conta utilizado Limite da Conta disponivel",
            "R$ -100,00 R$ 100,00 R$ 50,00 R$ 150,00",
            "data lancamentos valor (R$) saldo (R$)",
            "09/11/2026 PIX TRANSF FERNAND06/11 -1.250,30",
            "08/11/2026 SALDO DO DIA -100,00",
            "06/11/2026 PAGTO Salarios 3.150,00",
            "05/11/2026 PIX TRANSF MARCOS 05/11 10,00",
            "04/11/2026 IOF -0,55 -120,40");

        var lines = new PdfStatementParser().Parse(pdf);

        lines.Select(l => l.RawDescription).Should().Equal("PIX TRANSF FERNAND06/11", "PAGTO Salarios", "PIX TRANSF MARCOS 05/11", "IOF");
        lines.Should().OnlyContain(l => !l.IsFreeText);
        lines[0].Should().BeEquivalentTo(new { RawAmount = -1250.30m, RawDate = new DateOnly(2026, 11, 9) });
        lines[1].RawAmount.Should().Be(3150.00m);
        lines[2].RawAmount.Should().Be(10.00m, "positive values in a signed statement are credits");
        lines[3].RawAmount.Should().Be(-0.55m, "a trailing balance column is ignored");
    }

    [Fact]
    public void Unsigned_card_invoice_amounts_become_debits_except_credits()
    {
        var pdf = Pdf(
            "FATURA CARTAO OUTUBRO",
            "02/10 PGTO*IFOOD*SAOPAULO 45,90",
            "05/10 ESTORNO LOJA X 10,00",
            "TOTAL DA FATURA 35,90");

        var lines = new PdfStatementParser().Parse(pdf);

        lines.Should().HaveCount(2);
        lines[0].Should().BeEquivalentTo(new { RawDescription = "PGTO*IFOOD*SAOPAULO", RawAmount = -45.90m, RawDate = (DateOnly?)null, IsFreeText = false });
        lines[1].RawAmount.Should().Be(10.00m);
    }

    [Fact]
    public void Crafted_long_lines_do_not_exhaust_the_cpu()
    {
        // A backtracking regex took ~30s on this 56 KB line (ReDoS). Lines this long are never transactions.
        var hostile = "01/01/2026 X " + string.Concat(Enumerable.Repeat("1,00 a ", 8000)) + "x";
        var pdf = Pdf(hostile, "02/01/2026 PADARIA CENTRAL -12,50");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var lines = new PdfStatementParser().Parse(pdf);

        // Generous bound for parallel test runs; the vulnerable version took ~30s.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        lines.Should().ContainSingle().Which.RawDescription.Should().Be("PADARIA CENTRAL");
    }

    [Fact]
    public void Lines_without_a_leading_date_stay_free_text()
    {
        var lines = new PdfStatementParser().Parse(Pdf("Compra no dia 02 de outubro em LOJA XPTO no valor de 45,90 reais"));
        lines.Should().ContainSingle().Which.IsFreeText.Should().BeTrue();
    }
}
