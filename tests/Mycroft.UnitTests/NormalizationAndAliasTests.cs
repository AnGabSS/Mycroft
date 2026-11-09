using FluentAssertions;
using Mycroft.Core.Models;
using Mycroft.Core.Services;
using Mycroft.Infrastructure.Inference;

namespace Mycroft.UnitTests;

public class MerchantNormalizerTests
{
    private readonly MerchantNormalizer _sut = new();

    [Theory]
    [InlineData("PGTO*IFOOD*SAOPAULO*BR 45,90", "PGTO IFOOD SAOPAULO BR")]
    [InlineData("COMPRA CARTAO EUPIPIPOPO 45,90 02/10", "COMPRA CARTAO EUPIPIPOPO")]
    [InlineData("COMPRA ZEMERCADINHO 45,90 02/10", "COMPRA ZEMERCADINHO")]
    [InlineData("NETFLIX.COM PARC 02/10 R$ 1.234,56", "NETFLIX COM")]
    [InlineData("Padaria São João  NSU 123456 02/10/2026", "PADARIA SAO JOAO NSU")]
    [InlineData("UBER *TRIP HELP.UBER.COM AUT1234X", "UBER TRIP HELP UBER COM")]
    [InlineData("PIX TRANSF FERNAND06/11", "PIX TRANSF FERNAND")]   // date glued to a truncated name
    [InlineData("PIX QRS AUTOPASS S.01/11", "PIX QRS AUTOPASS S")]
    [InlineData("PIX QRS UBER DO BRA15/08", "PIX QRS UBER DO BRA")]
    public void Removes_variable_parts(string raw, string expected) =>
        _sut.Normalize(raw).Should().Be(expected);

    [Fact]
    public void Same_merchant_with_different_values_yields_same_signature() =>
        _sut.Normalize("PGTO*IFOOD*SAOPAULO*BR 45,90 02/10").Should().Be(_sut.Normalize("PGTO*IFOOD*SAOPAULO*BR 12,00 15/11"));

    [Fact]
    public void Same_pix_recipient_on_different_days_yields_same_signature() =>
        _sut.Normalize("PIX TRANSF FERNAND06/11").Should().Be(_sut.Normalize("PIX TRANSF Fernand29/10"));

    [Fact]
    public void Keeps_short_alphanumeric_merchant_names() =>
        _sut.Normalize("99POP 12,00").Should().Be("99POP");
}

public class AliasMatcherTests
{
    private static CustomCategory Food(params (string Pattern, string Merchant)[] aliases) => new()
    {
        Name = "Alimentação",
        Subcategories = ["Restaurante", "Delivery"],
        Aliases = aliases.Select(a => new CategoryAlias { Pattern = a.Pattern, MerchantName = a.Merchant, Subcategory = "Restaurante" }).ToList(),
    };

    [Fact]
    public void Longest_match_wins_regardless_of_order()
    {
        var sut = new AliasMatcher([Food(("%POPO%", "Lanchonete Popo"), ("%PIPIPOPO%", "Restaurante Pipipopo"))]);

        var match = sut.Match("COMPRA CARTAO EUPIPIPOPO");

        match!.Alias.MerchantName.Should().Be("Restaurante Pipipopo");
        match.CategoryName.Should().Be("Alimentação");
    }

    [Theory]
    [InlineData("IFOOD%", "IFOOD RESTAURANTE XPTO", true)]
    [InlineData("IFOOD%", "PGTO IFOOD SAOPAULO BR", true)]   // token starts with
    [InlineData("IFOOD%", "PGTOIFOOD", false)]
    [InlineData("%PIPIPOPO", "COMPRA EUPIPIPOPO", true)]
    [InlineData("%PIPIPOPO", "PIPIPOPOX", false)]
    [InlineData("%IFOOD%", "PGTOIFOODSP", true)]
    [InlineData("PIPIPOPO", "COMPRA PIPIPOPO SP", true)]     // exact token
    [InlineData("PIPIPOPO", "COMPRA EUPIPIPOPO", false)]
    [InlineData("PADOCA DO ZE", "COMPRA PADOCA DO ZE", true)] // multi-token exact
    [InlineData("padoca do zé", "PADOCA DO ZE", true)]        // case / accent insensitive
    public void Wildcards_follow_sql_like_semantics(string pattern, string signature, bool expected)
    {
        var sut = new AliasMatcher([Food((pattern, "X"))]);
        (sut.Match(signature) is not null).Should().Be(expected);
    }

    [Fact]
    public void Pattern_with_only_wildcards_is_ignored() =>
        new AliasMatcher([Food(("%%", "X"))]).IsEmpty.Should().BeTrue();
}
