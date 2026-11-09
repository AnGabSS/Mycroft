using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mycroft.Core.Entities;
using Mycroft.Core.Enums;
using Mycroft.Core.Interfaces;
using Mycroft.Core.Models;
using Mycroft.Infrastructure;
using Mycroft.Infrastructure.Inference;
using Mycroft.Infrastructure.Parsing;
using Mycroft.Infrastructure.Persistence;
using Mycroft.Infrastructure.Processing;
using Mycroft.Infrastructure.Webhooks;

namespace Mycroft.UnitTests;

public class ReconciliationProcessorTests
{
    private static readonly MerchantNormalizer Normalizer = new();

    private static StatementParserFactory Parsers() => new(
    [
        new CsvStatementParser(), new OfxStatementParser(), new CnabStatementParser(SourceFormat.Cnab240),
        new CnabStatementParser(SourceFormat.Cnab400), new PdfStatementParser(),
    ]);

    private static ReconciliationProcessor Processor(MycroftDbContext db, FakeChatClient chat, bool autoLearn = false)
    {
        var options = TestFactory.Options(o => o.SlmAutoLearn = autoLearn);
        return new(db, Parsers(), Normalizer, TestFactory.Inference(chat, options), options, NullLogger<ReconciliationProcessor>.Instance);
    }

    private static async Task<ReconciliationJob> IngestJsonAsync(MycroftDbContext db, IReadOnlyList<RawStatementLine> lines,
        IReadOnlyList<CustomCategory>? categories = null, string? callback = null)
    {
        var ingestion = new JobIngestionService(db, new FakeQueue(), Normalizer, TestFactory.Options());
        var result = await ingestion.IngestAsync(new NewJob(SourceFormat.JsonBatch, Guid.NewGuid().ToString("N").PadRight(64, '0'), null,
            categories is null ? null : JsonSerializer.Serialize(categories, MycroftJson.Options), callback, null, Lines: lines), default);
        return result.Job;
    }

    private static readonly CustomCategory[] SpecCategories =
    [
        new()
        {
            Name = "Alimentação",
            Subcategories = ["Restaurante", "Delivery", "Supermercado"],
            Aliases =
            [
                new() { Pattern = "IFOOD%", MerchantName = "iFood", Subcategory = "Delivery" },
                new() { Pattern = "%PIPIPOPO%", MerchantName = "Restaurante Pipipopo", Subcategory = "Restaurante" },
                new() { Pattern = "%POPO%", MerchantName = "Lanchonete Popo", Subcategory = "Restaurante" },
            ],
        },
    ];

    [Fact]
    public async Task Spec_example_resolves_by_alias_without_calling_the_slm()
    {
        await using var db = await TestFactory.SeededDbAsync();
        var chat = new FakeChatClient(_ => throw new InvalidOperationException("SLM must not be called"));
        var job = await IngestJsonAsync(db, [new RawStatementLine(0, "COMPRA CARTAO EUPIPIPOPO 45,90 02/10", -45.90m, new DateOnly(2026, 10, 2), "tx-01")],
            SpecCategories, callback: "https://client.example/webhook");

        await Processor(db, chat).ProcessAsync(job.Id, default);

        var saved = await db.ReconciliationJobs.Include(j => j.Items).SingleAsync(j => j.Id == job.Id);
        saved.Status.Should().Be(JobStatus.Completed);
        saved.AliasHits.Should().Be(1);
        saved.WebhookStatus.Should().Be(WebhookStatus.Pending);
        saved.Items.Single().Should().BeEquivalentTo(new
        {
            MerchantName = "Restaurante Pipipopo",
            Category = "Alimentação",
            Subcategory = "Restaurante",
            NormalizedSignature = "COMPRA CARTAO EUPIPIPOPO",
            ConfidenceScore = 1.000m,
            ResolutionSource = (ResolutionSource?)ResolutionSource.AliasMatch,
            TransactionType = (TransactionType?)TransactionType.Debit,
            PaymentMethod = (PaymentMethod?)PaymentMethod.CreditCard,
            NeedsReview = false,
        });
        chat.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Slm_answers_are_not_cached_unless_auto_learn_is_enabled()
    {
        await using var db = await TestFactory.SeededDbAsync();
        var chat = new FakeChatClient(m => FakeChatClient.ItemsJson(FakeChatClient.LineIndexes(m), confidence: 0.99m));

        var job = await IngestJsonAsync(db, [new RawStatementLine(0, "PGTO*IFOOD*SAOPAULO*BR 45,90", -45.90m)]);
        await Processor(db, chat).ProcessAsync(job.Id, default);

        (await db.MerchantPatterns.AnyAsync()).Should().BeFalse("a confident model can still be wrong; only feedback feeds the cache by default");
    }

    [Fact]
    public async Task Slm_results_are_learned_and_reused_from_cache_on_next_job_when_auto_learn_is_on()
    {
        await using var db = await TestFactory.SeededDbAsync();
        var chat = new FakeChatClient(m => FakeChatClient.ItemsJson(FakeChatClient.LineIndexes(m), confidence: 0.96m));

        var job1 = await IngestJsonAsync(db, [new RawStatementLine(0, "PGTO*IFOOD*SAOPAULO*BR 45,90", -45.90m, new DateOnly(2026, 10, 2))]);
        await Processor(db, chat, autoLearn: true).ProcessAsync(job1.Id, default);

        var pattern = await db.MerchantPatterns.SingleAsync();
        pattern.NormalizedSignature.Should().Be("PGTO IFOOD SAOPAULO BR");
        pattern.Category.Should().Be("Alimentação");

        var job2 = await IngestJsonAsync(db, [new RawStatementLine(0, "PGTO*IFOOD*SAOPAULO*BR 12,00", -12m, new DateOnly(2026, 10, 9))]);
        await Processor(db, chat, autoLearn: true).ProcessAsync(job2.Id, default);

        chat.Calls.Should().HaveCount(1, "second job is served from MerchantPatterns");
        var item = await db.TransactionItems.SingleAsync(i => i.JobId == job2.Id);
        item.ResolutionSource.Should().Be(ResolutionSource.Cache);
        item.Amount.Should().Be(-12m);
        (await db.ReconciliationJobs.SingleAsync(j => j.Id == job2.Id)).CacheHits.Should().Be(1);
        (await db.MerchantPatterns.SingleAsync()).HitCount.Should().Be(1);
    }

    [Fact]
    public async Task Cache_is_ignored_when_category_is_not_in_the_job_taxonomy()
    {
        await using var db = await TestFactory.SeededDbAsync();
        db.MerchantPatterns.Add(new MerchantPattern { NormalizedSignature = "NETFLIX COM", MerchantName = "Netflix", Category = "Assinaturas & Serviços Digitais", Subcategory = "Streaming", ConfidenceScore = 1m });
        await db.SaveChangesAsync();

        var custom = new CustomCategory { Name = "Lazer", Subcategories = ["Streaming"] };
        var chat = new FakeChatClient(m => FakeChatClient.ItemsJson(FakeChatClient.LineIndexes(m), category: "Lazer", sub: "Streaming", merchant: "Netflix"));
        var job = await IngestJsonAsync(db, [new RawStatementLine(0, "NETFLIX.COM", -39.90m)], [custom]);

        await Processor(db, chat).ProcessAsync(job.Id, default);

        var item = await db.TransactionItems.SingleAsync();
        item.ResolutionSource.Should().Be(ResolutionSource.SlmBatch);
        item.Category.Should().Be("Lazer");
        // the merchant name from the cache is still offered as a hint
        chat.Calls.Single().First(m => m.Role == Microsoft.Extensions.AI.ChatRole.User).Text.Should().Contain("Netflix");
        // tenant-private category never leaks into the global cache
        (await db.MerchantPatterns.SingleAsync()).Category.Should().Be("Assinaturas & Serviços Digitais");
    }

    [Fact]
    public async Task Positive_structural_amount_is_a_credit_even_if_the_model_says_debit()
    {
        await using var db = await TestFactory.SeededDbAsync();
        var chat = new FakeChatClient(m => FakeChatClient.ItemsJson(FakeChatClient.LineIndexes(m))); // always "Debit"
        var job = await IngestJsonAsync(db, [new RawStatementLine(0, "PIX TRANSF MARCOS 05/11", 10.00m), new RawStatementLine(1, "PIX TRANSF MARCOS 06/11", 20.00m)]);

        await Processor(db, chat).ProcessAsync(job.Id, default);

        (await db.TransactionItems.ToListAsync()).Should().OnlyContain(i => i.TransactionType == TransactionType.Credit);
    }

    [Fact]
    public async Task Duplicate_signatures_in_a_slice_hit_the_slm_once()
    {
        await using var db = await TestFactory.SeededDbAsync();
        var chat = new FakeChatClient(m => FakeChatClient.ItemsJson(FakeChatClient.LineIndexes(m), confidence: 0.5m));
        var lines = Enumerable.Range(0, 5).Select(i => new RawStatementLine(i, $"UBER *TRIP {i + 10},00", -(i + 10m))).ToList();
        var job = await IngestJsonAsync(db, lines);

        await Processor(db, chat).ProcessAsync(job.Id, default);

        FakeChatClient.LineIndexes(chat.Calls.Single()).Should().ContainSingle();
        var items = await db.TransactionItems.OrderBy(i => i.LineIndex).ToListAsync();
        // the fake answers "iFood", which is not grounded in "UBER *TRIP": the cleaned description is used instead
        items.Should().OnlyContain(i => i.MerchantName == "Uber Trip" && i.ProcessedAt != null);
        items.Select(i => i.Amount).Should().Equal(-10m, -11m, -12m, -13m, -14m);
    }

    [Fact]
    public async Task Fallback_items_mark_job_completed_with_warnings_and_raw_file_is_cleared()
    {
        await using var db = await TestFactory.SeededDbAsync();
        var chat = new FakeChatClient(_ => "garbage");
        var csv = "data;descricao;valor\n02/10/2026;LOJA X;-10,00\n";
        var ingestion = new JobIngestionService(db, new FakeQueue(), Normalizer, TestFactory.Options());
        var job = (await ingestion.IngestAsync(new NewJob(SourceFormat.Csv, new string('a', 64), null, null, null, null, "x.csv", Encoding.UTF8.GetBytes(csv)), default)).Job;

        await Processor(db, chat).ProcessAsync(job.Id, default);

        var saved = await db.ReconciliationJobs.SingleAsync();
        saved.Status.Should().Be(JobStatus.CompletedWithWarnings);
        saved.FallbackItems.Should().Be(1);
        saved.RawFilePayload.Should().BeNull();
        (await db.TransactionItems.SingleAsync()).Should().BeEquivalentTo(new { NeedsReview = true, ConfidenceScore = 0.200m, Amount = -10m });
    }

    [Fact]
    public async Task Unparseable_file_fails_the_job_with_a_clear_error()
    {
        await using var db = await TestFactory.SeededDbAsync();
        var ingestion = new JobIngestionService(db, new FakeQueue(), Normalizer, TestFactory.Options());
        var job = (await ingestion.IngestAsync(new NewJob(SourceFormat.Cnab240, new string('b', 64), null, null, "https://x/y", null, "x.ret", Encoding.ASCII.GetBytes("bad")), default)).Job;

        await Processor(db, new FakeChatClient(_ => "")).ProcessAsync(job.Id, default);

        var saved = await db.ReconciliationJobs.SingleAsync();
        saved.Status.Should().Be(JobStatus.Failed);
        saved.ErrorMessage.Should().Contain("Cnab240");
        saved.WebhookStatus.Should().Be(WebhookStatus.Pending, "failures are notified too");
    }

    [Fact]
    public async Task Resume_after_crash_processes_only_remaining_items()
    {
        await using var db = await TestFactory.SeededDbAsync();
        var lines = Enumerable.Range(0, 3).Select(i => new RawStatementLine(i, $"LOJA{(char)('A' + i)}", -1m)).ToList();
        var job = await IngestJsonAsync(db, lines);
        // Simulate a crash after the first item was checkpointed.
        var first = await db.TransactionItems.SingleAsync(i => i.LineIndex == 0);
        first.ProcessedAt = DateTimeOffset.UtcNow; first.ResolutionSource = ResolutionSource.Cache; first.Category = "Outros";
        job.Status = JobStatus.Processing;
        await db.SaveChangesAsync();

        var chat = new FakeChatClient(m => FakeChatClient.ItemsJson(FakeChatClient.LineIndexes(m)));
        await Processor(db, chat).ProcessAsync(job.Id, default);

        FakeChatClient.LineIndexes(chat.Calls.Single()).Should().Equal(1, 2);
        (await db.ReconciliationJobs.SingleAsync()).Status.Should().Be(JobStatus.Completed);
    }
}

public class JobIngestionServiceTests
{
    [Fact]
    public async Task Same_external_reference_returns_existing_job()
    {
        await using var db = await TestFactory.SeededDbAsync();
        var queue = new FakeQueue();
        var sut = new JobIngestionService(db, queue, new MerchantNormalizer(), TestFactory.Options());
        var request = new NewJob(SourceFormat.JsonBatch, new string('c', 64), "fatura-out-2026", null, null, null, Lines: [new RawStatementLine(0, "X", -1m)]);

        var first = await sut.IngestAsync(request, default);
        var second = await sut.IngestAsync(request with { PayloadHash = new string('d', 64) }, default);

        second.IsDuplicate.Should().BeTrue();
        second.Job.Id.Should().Be(first.Job.Id);
        queue.Enqueued.Should().ContainSingle();
    }

    [Fact]
    public async Task Resubmitting_a_failed_job_reference_reprocesses_it_in_place()
    {
        await using var db = await TestFactory.SeededDbAsync();
        var queue = new FakeQueue();
        var sut = new JobIngestionService(db, queue, new MerchantNormalizer(), TestFactory.Options());
        var first = await sut.IngestAsync(new NewJob(SourceFormat.JsonBatch, new string('m', 64), "fatura-nov-2026", null, null, null,
            Lines: [new RawStatementLine(0, "OLD", -1m)]), default);
        var failed = await db.ReconciliationJobs.SingleAsync();
        failed.Status = JobStatus.Failed;
        failed.ErrorMessage = "boom";
        await db.SaveChangesAsync();

        var retry = await sut.IngestAsync(new NewJob(SourceFormat.JsonBatch, new string('n', 64), "fatura-nov-2026", null, null, null,
            Lines: [new RawStatementLine(0, "NEW A", -2m), new RawStatementLine(1, "NEW B", -3m)]), default);

        retry.IsDuplicate.Should().BeFalse();
        retry.Job.Id.Should().Be(first.Job.Id);
        retry.Job.Status.Should().Be(JobStatus.Pending);
        retry.Job.ErrorMessage.Should().BeNull();
        (await db.TransactionItems.Select(i => i.RawDescription).ToListAsync()).Should().BeEquivalentTo(["NEW A", "NEW B"]);
        queue.Enqueued.Should().HaveCount(2);
    }

    [Fact]
    public async Task Resubmitting_a_finished_job_reference_still_returns_it()
    {
        await using var db = await TestFactory.SeededDbAsync();
        var sut = new JobIngestionService(db, new FakeQueue(), new MerchantNormalizer(), TestFactory.Options());
        var request = new NewJob(SourceFormat.JsonBatch, new string('o', 64), "ok-ref", null, null, null, Lines: [new RawStatementLine(0, "X", -1m)]);
        await sut.IngestAsync(request, default);
        (await db.ReconciliationJobs.SingleAsync()).Status = JobStatus.Completed;
        await db.SaveChangesAsync();

        (await sut.IngestAsync(request, default)).IsDuplicate.Should().BeTrue();
    }

    [Fact]
    public async Task New_external_reference_reprocesses_the_same_payload()
    {
        await using var db = await TestFactory.SeededDbAsync();
        var sut = new JobIngestionService(db, new FakeQueue(), new MerchantNormalizer(), TestFactory.Options());
        var request = new NewJob(SourceFormat.JsonBatch, new string('k', 64), "run-1", null, null, null, Lines: [new RawStatementLine(0, "X", -1m)]);

        var first = await sut.IngestAsync(request, default);
        var second = await sut.IngestAsync(request with { ExternalReferenceId = "run-2" }, default);

        second.IsDuplicate.Should().BeFalse();
        second.Job.Id.Should().NotBe(first.Job.Id);
    }

    [Fact]
    public async Task Same_payload_hash_within_window_is_deduplicated()
    {
        await using var db = await TestFactory.SeededDbAsync();
        var sut = new JobIngestionService(db, new FakeQueue(), new MerchantNormalizer(), TestFactory.Options());
        var request = new NewJob(SourceFormat.JsonBatch, new string('e', 64), null, null, null, null, Lines: [new RawStatementLine(0, "X", -1m)]);

        var first = await sut.IngestAsync(request, default);
        var second = await sut.IngestAsync(request, default);

        second.Job.Id.Should().Be(first.Job.Id);
        (await db.TransactionItems.CountAsync()).Should().Be(1);
    }
}

public class WebhookTests
{
    [Fact]
    public void Signature_is_hmac_sha256_lowercase_hex() =>
        // RFC-style known vector
        HmacWebhookDispatcher.ComputeSignature("key", "The quick brown fox jumps over the lazy dog")
            .Should().Be("f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8");

    [Fact]
    public async Task Failed_deliveries_follow_backoff_schedule_then_give_up()
    {
        await using var db = await TestFactory.SeededDbAsync();
        var time = new ManualTime(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));
        var job = new ReconciliationJob
        {
            PayloadHash = new string('f', 64), Status = JobStatus.Completed, CallbackUrl = "https://client/hook",
            WebhookStatus = WebhookStatus.Pending, NextWebhookRetryAt = time.GetUtcNow(),
        };
        db.ReconciliationJobs.Add(job);
        await db.SaveChangesAsync();

        var dispatcher = new FakeDispatcher(success: false);
        var sut = new WebhookDeliveryService(db, dispatcher, TestFactory.Options(), NullLogger<WebhookDeliveryService>.Instance, time);

        var expectedDelays = new[] { 15, 60, 300, 900, 3600 };
        foreach (var delay in expectedDelays)
        {
            (await sut.DeliverDueAsync(10, default)).Should().Be(1);
            (job.NextWebhookRetryAt! - time.GetUtcNow()).Value.TotalSeconds.Should().Be(delay);
            time.Advance(TimeSpan.FromSeconds(delay));
        }

        await sut.DeliverDueAsync(10, default);
        job.WebhookStatus.Should().Be(WebhookStatus.Failed);
        job.WebhookAttempts.Should().Be(6);
        dispatcher.Bodies.Should().HaveCount(6).And.OnlyContain(b => b.Contains("\"event\":\"reconciliation.completed\""));
    }

    [Fact]
    public async Task Successful_delivery_marks_delivered()
    {
        await using var db = await TestFactory.SeededDbAsync();
        var job = new ReconciliationJob { PayloadHash = new string('g', 64), Status = JobStatus.Completed, CallbackUrl = "https://c/h", WebhookStatus = WebhookStatus.Pending, NextWebhookRetryAt = DateTimeOffset.UtcNow.AddSeconds(-1) };
        db.ReconciliationJobs.Add(job);
        await db.SaveChangesAsync();

        await new WebhookDeliveryService(db, new FakeDispatcher(true), TestFactory.Options(), NullLogger<WebhookDeliveryService>.Instance).DeliverDueAsync(10, default);

        job.WebhookStatus.Should().Be(WebhookStatus.Delivered);
    }

    private sealed class FakeDispatcher(bool success) : IWebhookDispatcher
    {
        public List<string> Bodies { get; } = [];
        public Task<WebhookDeliveryResult> DispatchAsync(string callbackUrl, string? secret, string eventName, string jsonBody, CancellationToken cancellationToken = default)
        {
            Bodies.Add(jsonBody);
            return Task.FromResult(new WebhookDeliveryResult(success, success ? 200 : 500, success ? null : "HTTP 500"));
        }
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}

public class FeedbackServiceTests
{
    private static async Task<(MycroftDbContext Db, TransactionItem Item)> SetupAsync(string? customCategoriesJson = null)
    {
        var db = await TestFactory.SeededDbAsync();
        var job = new ReconciliationJob { PayloadHash = new string('h', 64), Status = JobStatus.Completed, CustomCategoriesJson = customCategoriesJson };
        var item = new TransactionItem { JobId = job.Id, RawDescription = "PGTO*ZE LANCHES 10,00", NormalizedSignature = "PGTO ZE LANCHES", Category = "Outros", ConfidenceScore = 0.2m, NeedsReview = true };
        db.ReconciliationJobs.Add(job);
        db.TransactionItems.Add(item);
        await db.SaveChangesAsync();
        return (db, item);
    }

    [Fact]
    public async Task Default_category_feedback_promotes_full_pattern()
    {
        var (db, item) = await SetupAsync();
        await using var _ = db;

        var result = await new FeedbackService(db).ApplyAsync(item.Id, new TransactionFeedback("Zé Lanches", "Alimentação", "Restaurante", PaymentMethod.Pix, null), default);

        result.Outcome.Should().Be(FeedbackOutcome.Updated);
        result.PromotedFullPattern.Should().BeTrue();
        (await db.MerchantPatterns.SingleAsync()).Should().BeEquivalentTo(new { MerchantName = "Zé Lanches", Category = "Alimentação", Subcategory = "Restaurante", ConfidenceScore = 1.000m, DefaultPaymentMethod = (PaymentMethod?)PaymentMethod.Pix });
        item.NeedsReview.Should().BeFalse();
    }

    [Fact]
    public async Task Custom_category_feedback_only_teaches_merchant_metadata()
    {
        var custom = JsonSerializer.Serialize(new[] { new CustomCategory { Name = "Lanches do Trabalho" } }, MycroftJson.Options);
        var (db, item) = await SetupAsync(custom);
        await using var _ = db;

        var result = await new FeedbackService(db).ApplyAsync(item.Id, new TransactionFeedback("Zé Lanches", "Lanches do Trabalho", null, null, true), default);

        result.PromotedFullPattern.Should().BeFalse();
        (await db.MerchantPatterns.SingleAsync()).Should().BeEquivalentTo(new { MerchantName = "Zé Lanches", Category = (string?)null, IsRecurring = true });
    }

    [Fact]
    public async Task Unknown_category_is_rejected()
    {
        var (db, item) = await SetupAsync();
        await using var _ = db;
        (await new FeedbackService(db).ApplyAsync(item.Id, new TransactionFeedback(null, "Nope", null, null, null), default))
            .Outcome.Should().Be(FeedbackOutcome.Invalid);
    }
}
