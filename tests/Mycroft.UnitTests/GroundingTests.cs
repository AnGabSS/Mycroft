using FluentAssertions;
using Mycroft.Infrastructure.Inference;

namespace Mycroft.UnitTests;

public class MerchantGroundingTests
{
    private static readonly MerchantNormalizer Normalizer = new();

    // Cases modeled on a real bank statement run with llama3.2:3b (names and values are fictitious).
    [Theory]
    [InlineData("iFood", "PAY IFD I 08/11", true)]                         // abbreviation
    [InlineData("Fernanda", "PIX TRANSF FERNAND06/11", true)]              // truncated name
    [InlineData("Tecnologia", "PIX QRS 99 TECNOLOG29/10", true)]
    [InlineData("Cristiano", "PIX TRANSF CRISTIA29/10", true)]
    [InlineData("Limite da Conta", "JUROS LIMITE DA CONTA", true)]
    [InlineData("Unibanco Holding S.A.", "PAG BOLETO ITAU UNIBANCO HOLDING S.A.", true)]
    [InlineData("Rendimentos", "REND PAGO APLIC AUT MAIS", true)]
    [InlineData("Mercado Livre", "MP*MERCADOLIVRE", true)]
    [InlineData("Salários", "PAGTO Salários", true)]
    [InlineData("Loja IFD I", "PAGTO Salários", false)]                     // leaked from a neighbour line
    [InlineData("Uber", "PAY LAN H 20/08", false)]
    [InlineData("Loja Exemplo", "PAY EMV C 12/10", false)]                  // prompt example copied
    [InlineData("iFood", "IOF", false)]
    public void Detects_merchants_unrelated_to_the_description(string merchant, string description, bool expected) =>
        MerchantGrounding.IsGrounded(merchant, description, Normalizer.Normalize(description)).Should().Be(expected);

    [Theory]
    [InlineData("Loja Bravo", "PAY BRAVO 07/11", "Bravo")]
    [InlineData("Loja Americanas", "LOJA AMERICANAS 123", "Loja Americanas")] // "LOJA" is really in the text
    [InlineData("Loja", "PAY X", "Loja")]                                       // never empties the name
    public void Strips_only_invented_prefixes(string merchant, string description, string expected) =>
        MerchantGrounding.StripInventedPrefixes(merchant, description).Should().Be(expected);
}
