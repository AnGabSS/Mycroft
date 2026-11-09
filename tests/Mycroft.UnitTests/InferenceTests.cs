using FluentAssertions;
using Mycroft.Core.Enums;
using Mycroft.Core.Models;
using Mycroft.Infrastructure.Inference;

namespace Mycroft.UnitTests;

public class OllamaTransactionInferenceServiceTests
{
    private static readonly InferenceContext Ctx = new(TestFactory.DefaultTaxonomy);

    [Fact]
    public async Task Valid_chunk_is_resolved_in_one_call()
    {
        var chat = new FakeChatClient(m => FakeChatClient.ItemsJson(FakeChatClient.LineIndexes(m)));
        var items = Enumerable.Range(0, 3).Select(i => TestFactory.Item(i, $"PGTO*IFOOD {i}")).ToList();

        var results = await TestFactory.Inference(chat).InferAsync(items, Ctx);

        chat.Calls.Should().HaveCount(1);
        results.Should().HaveCount(3).And.OnlyContain(r => r.Source == ResolutionSource.SlmBatch && r.Category == "Alimentação" && !r.NeedsReview);
    }

    [Fact]
    public async Task Chunks_respect_configured_size()
    {
        var chat = new FakeChatClient(m => FakeChatClient.ItemsJson(FakeChatClient.LineIndexes(m), merchant: null));
        var items = Enumerable.Range(0, 40).Select(i => TestFactory.Item(i, $"LOJA {(char)('A' + i)}")).ToList();

        await TestFactory.Inference(chat).InferAsync(items, Ctx);

        chat.Calls.Select(c => FakeChatClient.LineIndexes(c).Count).Should().Equal(15, 15, 10);
    }

    [Fact]
    public async Task Level1_repair_retry_receives_validation_error_and_fixes_items()
    {
        var call = 0;
        var chat = new FakeChatClient(m =>
        {
            call++;
            return call == 1 ? "{ not json" : FakeChatClient.ItemsJson(FakeChatClient.LineIndexes(m));
        });
        var items = Enumerable.Range(0, 2).Select(i => TestFactory.Item(i, $"LOJA {i}")).ToList();

        var results = await TestFactory.Inference(chat).InferAsync(items, Ctx);

        chat.Calls.Should().HaveCount(2);
        chat.Calls[1].Last().Text.Should().Contain("JSON inválido");
        results.Should().OnlyContain(r => r.Source == ResolutionSource.SlmBatch);
    }

    [Fact]
    public async Task Level2_isolates_the_bad_line_and_level3_falls_back_to_regex()
    {
        // Line 1 always gets an invalid category; other lines are fine.
        var chat = new FakeChatClient(m =>
        {
            var idx = FakeChatClient.LineIndexes(m);
            var good = FakeChatClient.ItemsJson(idx.Where(i => i != 1));
            return idx.Count == 1 && idx[0] == 1 ? FakeChatClient.ItemsJson(idx, category: "Categoria Inventada") : good;
        });
        var items = new[]
        {
            TestFactory.Item(0, "PGTO*IFOOD"),
            TestFactory.Item(1, "COMPRA MISTERIOSA PARC 02/10 45,90", amount: null, freeText: true),
            TestFactory.Item(2, "PGTO*IFOOD"),
        };

        var results = await TestFactory.Inference(chat).InferAsync(items, Ctx);

        results.Should().HaveCount(3);
        results[0].Source.Should().Be(ResolutionSource.SlmBatch);
        results[2].Source.Should().Be(ResolutionSource.SlmBatch);
        var fallback = results[1];
        fallback.Source.Should().Be(ResolutionSource.RegexFallback);
        fallback.ConfidenceScore.Should().Be(0.200m);
        fallback.NeedsReview.Should().BeTrue();
        fallback.Amount.Should().Be(-45.90m);
        fallback.InstallmentCurrent.Should().Be(2);
        fallback.InstallmentTotal.Should().Be(10);
        // chunk + repair + single attempt for line 1
        chat.Calls.Should().HaveCount(3);
    }

    [Fact]
    public async Task Same_merchant_copied_down_the_list_is_rejected_and_reasked_one_by_one()
    {
        // The model answers "iFood" for every line of a multi-item call, but answers correctly when isolated.
        var chat = new FakeChatClient(m =>
        {
            var idx = FakeChatClient.LineIndexes(m);
            return idx.Count > 1 ? FakeChatClient.ItemsJson(idx) : FakeChatClient.ItemsJson(idx, merchant: null);
        });
        string[] descriptions = ["PIX TRANSF MARCOS", "IOF", "JUROS LIMITE DA CONTA", "FATURA PAGA ITAU", "PAY IFD I"];
        var items = descriptions.Select((d, i) => TestFactory.Item(i, d)).ToList();

        var results = await TestFactory.Inference(chat).InferAsync(items, Ctx);

        results.Should().OnlyContain(r => r.Source == ResolutionSource.SlmSingleRetry && r.MerchantName != "iFood");
        chat.Calls[1].Last().Text.Should().Contain("repetido");
        chat.Calls.Should().HaveCount(2 + descriptions.Length);
    }

    [Fact]
    public async Task Same_merchant_imposed_by_hints_is_not_considered_degenerate()
    {
        var chat = new FakeChatClient(m => FakeChatClient.ItemsJson(FakeChatClient.LineIndexes(m)));
        var hint = new InferenceHint("iFood", null, null);
        var items = new[] { "IFOOD A", "IFOOD B", "IFOOD C", "IFOOD D" }.Select((d, i) => TestFactory.Item(i, d) with { Hint = hint }).ToList();

        var results = await TestFactory.Inference(chat).InferAsync(items, Ctx);

        chat.Calls.Should().ContainSingle();
        results.Should().OnlyContain(r => r.Source == ResolutionSource.SlmBatch);
    }

    [Fact]
    public async Task Merchant_leaked_from_another_line_is_replaced_and_sent_to_review()
    {
        var chat = new FakeChatClient(m => FakeChatClient.ItemsJson(FakeChatClient.LineIndexes(m), category: "Salário & Receitas", sub: "Salário", merchant: "Loja IFD I"));

        var result = (await TestFactory.Inference(chat).InferAsync([TestFactory.Item(0, "PAGTO Salários", amount: 3150.00m)], Ctx)).Single();

        result.MerchantName.Should().Be("Pagto Salarios");
        result.Category.Should().Be("Salário & Receitas", "the category is kept; only the merchant was unsupported");
        result.ConfidenceScore.Should().BeLessThanOrEqualTo(0.5m);
        result.NeedsReview.Should().BeTrue();
    }

    [Fact]
    public async Task Invented_prefix_is_stripped_from_a_grounded_merchant()
    {
        var chat = new FakeChatClient(m => FakeChatClient.ItemsJson(FakeChatClient.LineIndexes(m), merchant: "Loja Bravo"));
        var result = (await TestFactory.Inference(chat).InferAsync([TestFactory.Item(0, "PAY BRAVO 07/11")], Ctx)).Single();
        result.MerchantName.Should().Be("Bravo");
        result.NeedsReview.Should().BeFalse();
    }

    [Theory]
    [InlineData("TED 104.0000.CAIXA E F", PaymentMethod.Transfer)]
    [InlineData("PIX TRANSF MARCOS 06/11", PaymentMethod.Pix)]
    [InlineData("PAG BOLETO ITAU UNIBANCO", PaymentMethod.Boleto)]
    public async Task Explicit_payment_keywords_override_the_model(string description, PaymentMethod expected)
    {
        var chat = new FakeChatClient(m => FakeChatClient.ItemsJson(FakeChatClient.LineIndexes(m))); // always CreditCard
        var result = (await TestFactory.Inference(chat).InferAsync([TestFactory.Item(0, description)], Ctx)).Single();
        result.PaymentMethod.Should().Be(expected);
    }

    [Fact]
    public void Prompt_does_not_carry_a_real_brand_example() =>
        PromptTemplates.SystemPrompt(TestFactory.DefaultTaxonomy).Should().NotContainEquivalentOf("ifood").And.NotContainEquivalentOf("exemplo");

    [Fact]
    public async Task Single_retry_success_is_labelled_SlmSingleRetry()
    {
        // Line 1 is omitted by the chunk call and by the repair retry; only the isolated call answers it.
        var call = 0;
        var chat = new FakeChatClient(m =>
        {
            call++;
            var idx = FakeChatClient.LineIndexes(m);
            return call <= 2 ? FakeChatClient.ItemsJson(idx.Where(i => i != 1)) : FakeChatClient.ItemsJson(idx);
        });
        var items = Enumerable.Range(0, 3).Select(i => TestFactory.Item(i, $"LOJA {i}")).ToList();

        var results = await TestFactory.Inference(chat).InferAsync(items, Ctx);

        results[1].Source.Should().Be(ResolutionSource.SlmSingleRetry);
        chat.Calls.Should().HaveCount(3);
    }

    [Fact]
    public async Task Structural_amount_and_date_win_over_model_and_hints_override_category()
    {
        var chat = new FakeChatClient(m => FakeChatClient.ItemsJson(FakeChatClient.LineIndexes(m), category: "Outros", sub: "Diversos"));
        var item = TestFactory.Item(0, "COMPRA EUPIPIPOPO", amount: -99.99m) with
        {
            Hint = new InferenceHint("Restaurante Pipipopo", "Alimentação", "Restaurante"),
        };

        var result = (await TestFactory.Inference(chat).InferAsync([item], Ctx)).Single();

        result.Amount.Should().Be(-99.99m);
        result.TransactionType.Should().Be(TransactionType.Debit);
        result.MerchantName.Should().Be("Restaurante Pipipopo");
        result.Category.Should().Be("Alimentação");
        result.Subcategory.Should().Be("Restaurante");
    }

    [Fact]
    public async Task Installments_ignored_by_the_model_are_backfilled_from_the_description()
    {
        // ItemsJson always answers installmentCurrent/Total = null
        var chat = new FakeChatClient(m => FakeChatClient.ItemsJson(FakeChatClient.LineIndexes(m)));

        var result = (await TestFactory.Inference(chat).InferAsync([TestFactory.Item(0, "POSTO SHELL AV PAULISTA PARC 01/03")], Ctx)).Single();

        result.InstallmentCurrent.Should().Be(1);
        result.InstallmentTotal.Should().Be(3);
    }

    [Fact]
    public async Task Low_confidence_is_flagged_for_review()
    {
        var chat = new FakeChatClient(m => FakeChatClient.ItemsJson(FakeChatClient.LineIndexes(m), confidence: 0.4m));
        var result = (await TestFactory.Inference(chat).InferAsync([TestFactory.Item(0, "X")], Ctx)).Single();
        result.NeedsReview.Should().BeTrue();
    }

    [Fact]
    public async Task Transport_failure_skips_isolation_and_degrades_to_regex()
    {
        var chat = new FakeChatClient(_ => throw new HttpRequestException("connection refused"));
        var items = Enumerable.Range(0, 5).Select(i => TestFactory.Item(i, $"LOJA {i}")).ToList();

        var results = await TestFactory.Inference(chat).InferAsync(items, Ctx);

        chat.Calls.Should().HaveCount(1);
        results.Should().OnlyContain(r => r.Source == ResolutionSource.RegexFallback);
    }

    [Fact]
    public void Schema_constrains_categories_to_taxonomy()
    {
        var schema = PromptTemplates.BuildSchema([new TaxonomyEntry("Pets", ["Ração"])]);
        var item = schema.GetProperty("properties").GetProperty("items").GetProperty("items").GetProperty("properties");
        item.GetProperty("category").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).Should().Equal("Pets");
        item.GetProperty("subcategory").GetProperty("anyOf")[0].GetProperty("enum")[0].GetString().Should().Be("Ração");
    }

    [Fact]
    public void User_prompt_keeps_accents_readable() =>
        PromptTemplates.UserPrompt([TestFactory.Item(0, "PADARIA SÃO JOÃO")]).Should().Contain("SÃO JOÃO");

    [Fact]
    public void Code_fences_are_tolerated() =>
        SlmResponseValidator.StripCodeFences("```json\n{\"items\":[]}\n```").Should().Be("{\"items\":[]}");
}

public class RegexFallbackExtractorTests
{
    private readonly RegexFallbackExtractor _sut = new(new FixedTime(new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero)));

    [Theory]
    [InlineData("PIX ENVIADO JOAO 50,00", PaymentMethod.Pix)]
    [InlineData("PAGAMENTO BOLETO ENEL", PaymentMethod.Boleto)]
    [InlineData("TED ENVIADA 123", PaymentMethod.Transfer)]
    [InlineData("SAQUE 24H", PaymentMethod.Cash)]
    [InlineData("COMPRA CARTAO LOJA", PaymentMethod.CreditCard)]
    public void Infers_payment_method(string text, PaymentMethod expected) =>
        RegexFallbackExtractor.InferPaymentMethod(Mycroft.Core.Services.TextNormalization.ToUpperAsciiTokens(text)).Should().Be(expected);

    [Fact]
    public void Date_without_year_assumes_most_recent_past()
    {
        _sut.ExtractDate("COMPRA 02/10").Should().Be(new DateOnly(2026, 10, 2));
        _sut.ExtractDate("COMPRA 15/12").Should().Be(new DateOnly(2025, 12, 15));
    }

    [Fact]
    public void Credit_keywords_produce_positive_amount()
    {
        var r = _sut.Extract(TestFactory.Item(0, "PIX RECEBIDO MARIA 150,00", amount: null, freeText: true));
        r.Amount.Should().Be(150.00m);
        r.TransactionType.Should().Be(TransactionType.Credit);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
