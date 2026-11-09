using System.Globalization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Mycroft.Api.Channels;
using Mycroft.Api.Endpoints;
using Mycroft.Api.Security;
using Mycroft.Api.Workers;
using Mycroft.Core.Interfaces;
using Mycroft.Infrastructure;
using Mycroft.Infrastructure.Persistence;
using Mycroft.Infrastructure.Persistence.Seed;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ---- Options (flat env vars, see .env.example) ----
builder.Services.Configure<MycroftOptions>(o => BindOptions(o, config));
var maxUploadMb = Int(config, "MAX_UPLOAD_SIZE_MB") ?? 10;
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = (maxUploadMb + 1) * 1024L * 1024L);
builder.Services.Configure<FormOptions>(f => f.MultipartBodyLengthLimit = (maxUploadMb + 1) * 1024L * 1024L);

// ---- Infrastructure ----
var connectionString = config.GetConnectionString("Mycroft")
                       ?? config["MYCROFT_DB_CONNECTION"]
                       ?? "Host=localhost;Port=5432;Database=mycroft;Username=mycroft;Password=mycroft";
builder.Services.AddSingleton<IReconciliationJobQueue, InMemoryJobQueue>();
builder.Services.AddMycroftInfrastructure(connectionString);

// ---- Workers ----
builder.Services.AddHostedService<ReconciliationWorker>();
builder.Services.AddHostedService<WebhookRetryWorker>();
builder.Services.AddHostedService<RetentionCleanupWorker>();
builder.Services.AddHostedService<SlmWarmupWorker>();

// ---- API ----
builder.Services.ConfigureHttpJsonOptions(o => MycroftJson.Configure(o.SerializerOptions));
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
{
    // Binding failures (malformed JSON, multipart without boundary...) would otherwise be a bare "Bad Request".
    if (ctx.Exception is BadHttpRequestException bad)
    {
        ctx.ProblemDetails.Detail = bad.InnerException is { } inner ? $"{bad.Message} {inner.Message}" : bad.Message;
        if (bad.InnerException is InvalidDataException || bad.Message.Contains("form", StringComparison.OrdinalIgnoreCase))
            ctx.ProblemDetails.Extensions["hint"] =
                "Send multipart/form-data with the file in a field named 'file' (type File). Do not set the Content-Type header manually: the client must add the boundary.";
    }
});
// Surface binding errors as exceptions so the problem details above can explain them.
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.Services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
{
    doc.Info.Title = "Mycroft — Financial Reconciliation via SLM";
    doc.Info.Description = "Asynchronous, on-premise engine that turns chaotic bank/card/CNAB descriptions into structured, categorized transactions using a local Small Language Model (Ollama).";
    doc.Info.Version = "v1";
    return Task.CompletedTask;
}).AddApiKeySecurityScheme(enabled: ApiKeyAuthentication.GetConfiguredKey(config) is not null));
builder.Services.AddMycroftHealthChecks();

var app = builder.Build();

app.UseExceptionHandler(new ExceptionHandlerOptions
{
    StatusCodeSelector = ex => ex is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError,
});
app.UseStatusCodePages();
app.UseMycroftApiKey();

app.MapOpenApi();
app.MapScalarApiReference(o => o.WithTitle("Mycroft API"));
app.MapGet("/", () => Results.Redirect("/scalar/v1")).ExcludeFromDescription();

app.MapReconciliationEndpoints();
app.MapCategoryEndpoints();
app.MapMycroftHealth();

await InitializeDatabaseAsync(app);
await app.RunAsync();
return;

static async Task InitializeDatabaseAsync(WebApplication app)
{
    if (string.Equals(app.Configuration["DB_AUTO_MIGRATE"], "false", StringComparison.OrdinalIgnoreCase)) return;

    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<MycroftDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    // Postgres may still be starting when the container boots: retry for ~60s.
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            if (db.Database.IsRelational()) await db.Database.MigrateAsync();
            else await db.Database.EnsureCreatedAsync();
            await CategorySeeder.SeedAsync(db);
            return;
        }
        catch (Exception ex) when (attempt < 12)
        {
            logger.LogWarning("Database not ready (attempt {Attempt}): {Message}", attempt, ex.Message);
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }
}

static void BindOptions(MycroftOptions o, IConfiguration c)
{
    o.OllamaEndpoint = c["OLLAMA_ENDPOINT"] ?? o.OllamaEndpoint;
    o.OllamaModel = c["OLLAMA_MODEL"] ?? o.OllamaModel;
    o.MaxConcurrentSlmRequests = Int(c, "MAX_CONCURRENT_SLM_REQUESTS") ?? o.MaxConcurrentSlmRequests;
    o.SlmChunkSize = Math.Clamp(Int(c, "SLM_CHUNK_SIZE") ?? o.SlmChunkSize, 1, 50);
    o.SlmRequestTimeout = Int(c, "SLM_REQUEST_TIMEOUT_SECONDS") is { } t ? TimeSpan.FromSeconds(t) : o.SlmRequestTimeout;
    o.SlmAutoLearn = string.Equals(c["SLM_AUTO_LEARN"], "true", StringComparison.OrdinalIgnoreCase);
    o.SlmWarmup = !string.Equals(c["SLM_WARMUP"], "false", StringComparison.OrdinalIgnoreCase);
    o.NeedsReviewThreshold = decimal.TryParse(c["NEEDS_REVIEW_THRESHOLD"], NumberStyles.Number, CultureInfo.InvariantCulture, out var th) ? th : o.NeedsReviewThreshold;
    o.RetentionDays = Int(c, "RETENTION_DAYS") ?? o.RetentionDays;
    o.MaxUploadSizeMb = Int(c, "MAX_UPLOAD_SIZE_MB") ?? o.MaxUploadSizeMb;
    o.MaxItemsPerJob = Int(c, "MAX_ITEMS_PER_JOB") ?? o.MaxItemsPerJob;
    o.WorkerConcurrency = Int(c, "WORKER_CONCURRENCY") ?? o.WorkerConcurrency;
    o.DefaultWebhookSecret = c["WEBHOOK_DEFAULT_SECRET"] is { Length: > 0 } s ? s : o.DefaultWebhookSecret;
    o.WebhookTimeout = Int(c, "WEBHOOK_TIMEOUT_SECONDS") is { } wt ? TimeSpan.FromSeconds(wt) : o.WebhookTimeout;
}

static int? Int(IConfiguration c, string key) =>
    int.TryParse(c[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

public partial class Program;
