using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Mycroft.Infrastructure;

namespace Mycroft.IntegrationTests;

public sealed class ReconciliationApiTests(MycroftApiFactory factory) : IClassFixture<MycroftApiFactory>
{
    private HttpClient Client()
    {
        var client = AnonymousClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", MycroftApiFactory.ApiKey);
        return client;
    }

    private HttpClient AnonymousClient()
    {
        Skip.IfNot(factory.DockerAvailable, $"Docker is not available: {factory.DockerError}");
        return factory.CreateClient();
    }

    private static object SpecPayload(string reference) => new
    {
        externalReferenceId = reference,
        callbackUrl = "https://api.vezta.app/webhooks/mycroft",
        webhookSecret = "s3cr3t",
        customCategories = new[]
        {
            new
            {
                name = "Alimentação",
                subcategories = new[] { "Restaurante", "Delivery", "Supermercado" },
                aliases = new object[]
                {
                    new { pattern = "IFOOD%", merchantName = "iFood", subcategory = "Delivery" },
                    new { pattern = "%PIPIPOPO%", merchantName = "Restaurante Pipipopo", subcategory = "Restaurante" },
                    new { pattern = "%POPO%", merchantName = "Lanchonete Popo", subcategory = "Restaurante" },
                },
            },
        },
        transactions = new object[]
        {
            new { externalLineId = "tx-01", rawDescription = "COMPRA CARTAO EUPIPIPOPO 45,90 02/10", rawAmount = -45.90m, rawDate = "2026-10-02" },
            new { externalLineId = "tx-02", rawDescription = "MERCADO DESCONHECIDO 10,00", rawAmount = -10.00m, rawDate = "2026-10-03" },
        },
    };

    private static async Task<JsonElement> WaitForCompletionAsync(HttpClient client, string statusUrl)
    {
        for (var i = 0; i < 100; i++)
        {
            var job = await client.GetFromJsonAsync<JsonElement>(statusUrl);
            if (job.GetProperty("status").GetString() is "Completed" or "CompletedWithWarnings" or "Failed") return job;
            await Task.Delay(100);
        }
        throw new TimeoutException("Job did not finish in time");
    }

    [SkippableFact]
    public async Task Batch_is_accepted_processed_and_notified()
    {
        var client = Client();

        var response = await client.PostAsJsonAsync("/api/v1/reconciliations/batch", SpecPayload("fatura-out-2026"));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>();
        var statusUrl = accepted.GetProperty("statusUrl").GetString()!;
        response.Headers.Location!.ToString().Should().Be(statusUrl);

        var job = await WaitForCompletionAsync(client, statusUrl);

        job.GetProperty("status").GetString().Should().Be("Completed");
        job.GetProperty("metrics").GetProperty("aliasHits").GetInt32().Should().Be(1);
        var txs = job.GetProperty("transactions").EnumerateArray().ToList();
        txs[0].GetProperty("merchantName").GetString().Should().Be("Restaurante Pipipopo");
        txs[0].GetProperty("resolutionSource").GetString().Should().Be("AliasMatch");
        txs[1].GetProperty("resolutionSource").GetString().Should().Be("SlmBatch");
        txs[1].GetProperty("amount").GetDecimal().Should().Be(-10.00m);

        // webhook delivered by the background worker
        for (var i = 0; i < 50 && factory.Webhooks.Deliveries.IsEmpty; i++) await Task.Delay(100);
        var delivery = factory.Webhooks.Deliveries.Should().ContainSingle(d => d.Body.Contains(accepted.GetProperty("jobId").GetString()!)).Subject;
        delivery.Secret.Should().Be("s3cr3t");
        delivery.Event.Should().Be("reconciliation.completed");
    }

    [SkippableFact]
    public async Task Same_external_reference_returns_the_existing_job()
    {
        var client = Client();

        var first = await (await client.PostAsJsonAsync("/api/v1/reconciliations/batch", SpecPayload("dup-ref"))).Content.ReadFromJsonAsync<JsonElement>();
        await WaitForCompletionAsync(client, first.GetProperty("statusUrl").GetString()!);
        var secondResponse = await client.PostAsJsonAsync("/api/v1/reconciliations/batch", SpecPayload("dup-ref"));
        var second = await secondResponse.Content.ReadFromJsonAsync<JsonElement>();

        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK, "the original job is already finished");
        second.GetProperty("jobId").GetString().Should().Be(first.GetProperty("jobId").GetString());
        second.GetProperty("duplicate").GetBoolean().Should().BeTrue();
    }

    [SkippableFact]
    public async Task Csv_upload_is_parsed_and_reconciled()
    {
        var client = Client();
        var csv = "data;descricao;valor\n02/10/2026;PGTO*LOJA*ABC;-12,34\n03/10/2026;PGTO*LOJA*DEF;-56,78\n";
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", "extrato.csv");

        var response = await client.PostAsync("/api/v1/reconciliations/upload", form);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>();
        var job = await WaitForCompletionAsync(client, accepted.GetProperty("statusUrl").GetString()!);
        job.GetProperty("sourceFormat").GetString().Should().Be("Csv");
        job.GetProperty("metrics").GetProperty("totalItems").GetInt32().Should().Be(2);
        job.GetProperty("transactions")[1].GetProperty("amount").GetDecimal().Should().Be(-56.78m);
    }

    [SkippableFact]
    public async Task Feedback_promotes_pattern_and_next_job_hits_cache()
    {
        var client = Client();
        var payload = new { transactions = new[] { new { rawDescription = "PGTO*PADARIA*CENTRAL 7,50", rawAmount = -7.50m } } };
        var accepted = await (await client.PostAsJsonAsync("/api/v1/reconciliations/batch", payload)).Content.ReadFromJsonAsync<JsonElement>();
        var job = await WaitForCompletionAsync(client, accepted.GetProperty("statusUrl").GetString()!);
        var txId = job.GetProperty("transactions")[0].GetProperty("id").GetString();

        var feedback = await client.PatchAsJsonAsync($"/api/v1/transactions/{txId}/feedback",
            new { merchantName = "Padaria Central", category = "Alimentação", subcategory = "Padaria/Café" });
        feedback.StatusCode.Should().Be(HttpStatusCode.OK);
        (await feedback.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("promotedToGlobalCache").GetBoolean().Should().BeTrue();

        var payload2 = new { transactions = new[] { new { rawDescription = "PGTO*PADARIA*CENTRAL 3,20", rawAmount = -3.20m } } };
        var accepted2 = await (await client.PostAsJsonAsync("/api/v1/reconciliations/batch", payload2)).Content.ReadFromJsonAsync<JsonElement>();
        var job2 = await WaitForCompletionAsync(client, accepted2.GetProperty("statusUrl").GetString()!);
        var tx = job2.GetProperty("transactions")[0];
        tx.GetProperty("resolutionSource").GetString().Should().Be("Cache");
        tx.GetProperty("merchantName").GetString().Should().Be("Padaria Central");
        tx.GetProperty("subcategory").GetString().Should().Be("Padaria/Café");
    }

    [SkippableFact]
    public async Task Invalid_batch_returns_validation_problem()
    {
        var client = Client();
        var response = await client.PostAsJsonAsync("/api/v1/reconciliations/batch", new { transactions = Array.Empty<object>(), callbackUrl = "ftp://nope" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("errors").TryGetProperty("transactions", out _).Should().BeTrue();
        problem.GetProperty("errors").TryGetProperty("callbackUrl", out _).Should().BeTrue();
    }

    [SkippableFact]
    public async Task Upload_with_wrong_field_name_explains_what_was_received()
    {
        var client = Client();
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("a;b\n1;2\n")), "arquivo", "x.csv");
        form.Add(new StringContent("file.csv"), "file");

        var response = await client.PostAsync("/api/v1/reconciliations/upload", form);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var message = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").GetProperty("file")[0].GetString();
        message.Should().Contain("'arquivo' (File)").And.Contain("'file' (Text)");
    }

    [SkippableFact]
    public async Task Multipart_without_boundary_returns_a_hint()
    {
        var client = Client();
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes("raw bytes"));
        content.Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data");

        var response = await client.PostAsync("/api/v1/reconciliations/upload", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("detail").GetString().Should().NotBeNullOrEmpty();
        problem.GetProperty("hint").GetString().Should().Contain("Content-Type");
    }

    [SkippableTheory]
    [InlineData(null)]
    [InlineData("wrong-key")]
    public async Task Api_requires_the_configured_key(string? key)
    {
        var client = AnonymousClient();
        if (key is not null) client.DefaultRequestHeaders.Add("X-Api-Key", key);

        (await client.GetAsync("/api/v1/categories")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/api/v1/categories", new { name = "Hacked" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [SkippableFact]
    public async Task Health_and_docs_stay_public()
    {
        var client = AnonymousClient();
        (await client.GetAsync("/openapi/v1.json")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/health")).StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    [SkippableFact]
    public async Task Categories_are_seeded_and_can_be_extended()
    {
        var client = Client();
        var categories = await client.GetFromJsonAsync<JsonElement>("/api/v1/categories");
        categories.EnumerateArray().Should().HaveCountGreaterThanOrEqualTo(11);

        var created = await client.PostAsJsonAsync("/api/v1/categories", new { name = "Pets", subcategories = new[] { "Ração", "Veterinário" } });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        (await client.PostAsJsonAsync("/api/v1/categories", new { name = "Pets" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [SkippableFact]
    public async Task Openapi_document_is_served()
    {
        var client = Client();
        var doc = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json", MycroftJson.Options);
        doc.GetProperty("paths").TryGetProperty("/api/v1/reconciliations/batch", out _).Should().BeTrue();
    }
}
