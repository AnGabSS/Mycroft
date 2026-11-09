using System.Globalization;
using System.Text.RegularExpressions;
using Mycroft.Core.Enums;
using Mycroft.Core.Models;
using Mycroft.Core.Services;
using Mycroft.Infrastructure.Parsing;
using Mycroft.Infrastructure.Persistence.Seed;

namespace Mycroft.Infrastructure.Inference;

/// <summary>
/// Last-resort, deterministic extractor (fallback level 3). Never fails: it always produces a result flagged
/// for review with low confidence. Also provides the heuristics used to complete alias/cache resolutions.
/// </summary>
public sealed partial class RegexFallbackExtractor(TimeProvider? timeProvider = null)
{
    public const decimal FallbackConfidence = 0.200m;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    [GeneratedRegex(@"\bPARC(?:ELA)?\.?\s*(?<cur>\d{1,2})\s*(?:/|DE)\s*(?<tot>\d{1,2})\b|\b(?<cur2>\d{1,2})\s*DE\s*(?<tot2>\d{1,2})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstallmentRegex();

    private static readonly string[] CreditKeywords =
        ["RECEBIDO", "RECEBIDA", "CREDITO", "ESTORNO", "SALARIO", "DEPOSITO", "RENDIMENTO", "REEMBOLSO", "PIX RECEB", "TED RECEB", "DOC RECEB", "LIQUIDACAO BOLETO"];

    public ReconciledTransactionResult Extract(InferenceRequestItem item)
    {
        var line = item.Line;
        var upper = TextNormalization.ToUpperAsciiTokens(line.RawDescription);

        var amount = line.RawAmount ?? ExtractAmount(line.RawDescription);
        var type = line.RawAmount is { } structural ? TypeFromSign(structural) : InferTransactionType(amount, upper);
        if (amount is not null && line.RawAmount is null)
            amount = type == TransactionType.Credit ? Math.Abs(amount.Value) : -Math.Abs(amount.Value);

        var (current, total) = ExtractInstallments(line.RawDescription);

        return new ReconciledTransactionResult
        {
            LineIndex = line.LineIndex,
            MerchantName = item.Hint?.MerchantName ?? ToTitle(item.NormalizedSignature),
            Amount = amount,
            TransactionDate = line.RawDate ?? ExtractDate(line.RawDescription),
            TransactionType = type,
            Category = item.Hint?.Category ?? CategorySeeder.FallbackCategory,
            Subcategory = item.Hint?.Category is not null ? item.Hint.Subcategory : CategorySeeder.FallbackSubcategory,
            PaymentMethod = InferPaymentMethod(upper),
            InstallmentCurrent = current,
            InstallmentTotal = total,
            IsRecurring = false,
            ConfidenceScore = FallbackConfidence,
            NeedsReview = true,
            Source = ResolutionSource.RegexFallback,
        };
    }

    public static decimal? ExtractAmount(string text)
    {
        var matches = ParsingHelpers.MoneyRegex().Matches(text);
        return matches.Count == 0 ? null : ParsingHelpers.ParseAmount(matches[^1].Value);
    }

    public DateOnly? ExtractDate(string text)
    {
        var m = ParsingHelpers.ShortDateRegex().Match(text);
        if (!m.Success) return null;

        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        var day = int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
        int year;
        if (m.Groups["y"].Success)
        {
            year = int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture);
            if (year < 100) year += 2000;
        }
        else
        {
            // Statements list past transactions: assume the most recent year that does not land in the future.
            year = today.Year;
            if (month > today.Month || (month == today.Month && day > today.Day)) year--;
        }

        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) return null;
        return new DateOnly(year, month, day);
    }

    public static (int? Current, int? Total) ExtractInstallments(string text)
    {
        var m = InstallmentRegex().Match(text);
        if (!m.Success) return (null, null);

        var cur = m.Groups["cur"].Success ? m.Groups["cur"].Value : m.Groups["cur2"].Value;
        var tot = m.Groups["tot"].Success ? m.Groups["tot"].Value : m.Groups["tot2"].Value;
        var c = int.Parse(cur, CultureInfo.InvariantCulture);
        var t = int.Parse(tot, CultureInfo.InvariantCulture);
        return c >= 1 && t >= 2 && c <= t ? (c, t) : (null, null);
    }

    /// <summary>Sign of an amount that came from the source (signed convention: negative = debit).</summary>
    public static TransactionType TypeFromSign(decimal amount) => amount < 0 ? TransactionType.Debit : TransactionType.Credit;

    public static TransactionType? InferTransactionType(decimal? amount, string upperDescription)
    {
        if (amount is < 0) return TransactionType.Debit;
        if (CreditKeywords.Any(k => upperDescription.Contains(k, StringComparison.Ordinal))) return TransactionType.Credit;
        if (amount is > 0) return TransactionType.Debit; // unsigned values in card statements/PDFs are purchases
        return null;
    }

    /// <summary>
    /// Payment method stated explicitly by the description (PIX, TED/DOC, BOLETO, SAQUE). These keywords are
    /// unambiguous, so they override the model. Returns null when the text does not say it.
    /// </summary>
    public static PaymentMethod? StrongPaymentMethod(string upperDescription)
    {
        bool Has(params string[] words) => words.Any(w => $" {upperDescription} ".Contains($" {w} ", StringComparison.Ordinal));

        if (Has("PIX")) return PaymentMethod.Pix;
        if (Has("BOLETO")) return PaymentMethod.Boleto;
        if (Has("TED", "DOC")) return PaymentMethod.Transfer;
        if (Has("SAQUE")) return PaymentMethod.Cash;
        return null;
    }

    public static PaymentMethod InferPaymentMethod(string upperDescription)
    {
        bool Has(params string[] words) => words.Any(w => $" {upperDescription} ".Contains($" {w}", StringComparison.Ordinal));

        if (Has("PIX")) return PaymentMethod.Pix;
        if (Has("BOLETO", "TITULO", "LIQUIDACAO")) return PaymentMethod.Boleto;
        if (Has("TED", "DOC", "TRANSF", "TRANSFERENCIA")) return PaymentMethod.Transfer;
        if (Has("SAQUE")) return PaymentMethod.Cash;
        if (Has("DEBITO", "COMPRA DEB", "ELO DEBITO", "MAESTRO")) return PaymentMethod.DebitCard;
        if (Has("CARTAO", "CREDITO", "COMPRA", "PGTO", "PAG")) return PaymentMethod.CreditCard;
        return PaymentMethod.Other;
    }

    private static string? ToTitle(string signature) =>
        string.IsNullOrWhiteSpace(signature)
            ? null
            : CultureInfo.GetCultureInfo("pt-BR").TextInfo.ToTitleCase(signature.ToLowerInvariant());
}
