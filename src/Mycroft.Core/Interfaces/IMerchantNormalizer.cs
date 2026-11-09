namespace Mycroft.Core.Interfaces;

public interface IMerchantNormalizer
{
    /// <summary>Produces a stable signature by removing dates, amounts, installments and variable codes.</summary>
    string Normalize(string rawDescription);
}
