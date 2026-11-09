using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mycroft.Api.Contracts;
using Mycroft.Core.Enums;
using Mycroft.Core.Interfaces;
using Mycroft.Core.Models;
using Mycroft.Infrastructure;
using Mycroft.Infrastructure.Persistence;
using Mycroft.Infrastructure.Processing;

namespace Mycroft.Api.Endpoints;

public static class ReconciliationEndpoints
{
    public static IEndpointRouteBuilder MapReconciliationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1").WithTags("Reconciliations");

        group.MapPost("/reconciliations/batch", CreateBatchAsync)
            .WithName("CreateBatchReconciliation")
            .WithSummary("Submit a JSON batch of raw transactions")
            .WithDescription("Persists the batch (inbox) and returns 202 immediately. Idempotent by externalReferenceId and by payload hash (24h).");

        group.MapPost("/reconciliations/upload", UploadAsync)
            .WithName("UploadStatementFile")
            .WithSummary("Upload a CSV, OFX, CNAB 240/400 or PDF statement")
            .WithDescription("multipart/form-data. Fields: file (required), format (optional: Csv|Ofx|Cnab240|Cnab400|Pdf, auto-detected), externalReferenceId, callbackUrl, webhookSecret, customCategories (JSON array).")
            .DisableAntiforgery();

        group.MapGet("/reconciliations/{jobId:guid}", GetJobAsync)
            .WithName("GetReconciliation")
            .WithSummary("Job status, metrics and reconciled transactions");

        group.MapPatch("/transactions/{transactionId:guid}/feedback", FeedbackAsync)
            .WithTags("Feedback")
            .WithName("SubmitTransactionFeedback")
            .WithSummary("Correct a transaction and teach the merchant cache");

        return app;
    }

    private static async Task<Results<Accepted<JobAcceptedResponse>, Ok<JobAcceptedResponse>, ValidationProblem>> CreateBatchAsync(
        BatchReconciliationRequest request,
        JobIngestionService ingestion,
        IOptions<MycroftOptions> options,
        CancellationToken ct)
    {
        var errors = RequestValidation.ValidateBatch(request, options.Value.MaxItemsPerJob);
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

        var canonical = JsonSerializer.SerializeToUtf8Bytes(request, MycroftJson.Options);
        var lines = request.Transactions
            .Select((t, i) => new RawStatementLine(i, t.RawDescription.Trim(), t.RawAmount, t.RawDate, t.ExternalLineId))
            .ToList();

        var result = await ingestion.IngestAsync(new NewJob(
            SourceFormat.JsonBatch,
            JobIngestionService.Sha256Hex(canonical),
            NullIfBlank(request.ExternalReferenceId),
            SerializeCategories(request.CustomCategories),
            NullIfBlank(request.CallbackUrl),
            NullIfBlank(request.WebhookSecret),
            Lines: lines), ct);

        return ToAcceptedResult(result);
    }

    private static async Task<Results<Accepted<JobAcceptedResponse>, Ok<JobAcceptedResponse>, ValidationProblem>> UploadAsync(
        IFormFile? file,
        [FromForm] string? format,
        [FromForm] string? externalReferenceId,
        [FromForm] string? callbackUrl,
        [FromForm] string? webhookSecret,
        [FromForm] string? customCategories,
        JobIngestionService ingestion,
        IStatementParserFactory parsers,
        IOptions<MycroftOptions> options,
        HttpRequest request,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var form = request.HasFormContentType ? await request.ReadFormAsync(ct) : null; // already parsed by binding
        var maxBytes = options.Value.MaxUploadSizeMb * 1024L * 1024L;

        if (file is null)
            errors["file"] = [$"No file received. Send it in a multipart/form-data field named 'file' of type File (received fields: {Describe(form)})."];
        else if (file.Length == 0) errors["file"] = ["The file is empty."];
        else if (file.Length > maxBytes) errors["file"] = [$"File exceeds MAX_UPLOAD_SIZE_MB={options.Value.MaxUploadSizeMb}."];

        IReadOnlyList<CustomCategory>? categories = null;
        if (!string.IsNullOrWhiteSpace(customCategories))
        {
            try { categories = JsonSerializer.Deserialize<List<CustomCategory>>(customCategories, MycroftJson.Options); }
            catch (JsonException ex) { errors["customCategories"] = [$"Invalid JSON: {ex.Message}"]; }
        }

        RequestValidation.ValidateCommon(externalReferenceId, NullIfBlank(callbackUrl), webhookSecret, categories, errors);
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

        byte[] bytes;
        await using (var stream = file!.OpenReadStream())
        using (var ms = new MemoryStream((int)file.Length))
        {
            await stream.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }

        SourceFormat? sourceFormat;
        if (!string.IsNullOrWhiteSpace(format))
        {
            sourceFormat = Enum.TryParse<SourceFormat>(format, ignoreCase: true, out var f) && f != SourceFormat.JsonBatch ? f : null;
            if (sourceFormat is null)
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["format"] = ["format must be one of Csv, Ofx, Cnab240, Cnab400, Pdf."] });
        }
        else
        {
            sourceFormat = parsers.Detect(file.FileName, bytes);
            if (sourceFormat is null)
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["format"] = ["Could not detect the file format; send the 'format' field."] });
        }

        var categoriesJson = SerializeCategories(categories);
        // Hash covers the file and the job-defining metadata, so the same file with another taxonomy/callback is a new job.
        var hashInput = bytes.Concat(Encoding.UTF8.GetBytes($"|{sourceFormat}|{categoriesJson}|{callbackUrl}")).ToArray();

        var result = await ingestion.IngestAsync(new NewJob(
            sourceFormat.Value,
            JobIngestionService.Sha256Hex(hashInput),
            NullIfBlank(externalReferenceId),
            categoriesJson,
            NullIfBlank(callbackUrl),
            NullIfBlank(webhookSecret),
            OriginalFileName: Path.GetFileName(file.FileName) is { Length: > 255 } n ? n[..255] : Path.GetFileName(file.FileName),
            RawFile: bytes), ct);

        return ToAcceptedResult(result);
    }

    private static async Task<Results<Ok<JobResultView>, NotFound>> GetJobAsync(
        Guid jobId, MycroftDbContext db, CancellationToken ct, [FromQuery] bool includeTransactions = true)
    {
        var job = await db.ReconciliationJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return TypedResults.NotFound();

        var items = includeTransactions
            ? await db.TransactionItems.AsNoTracking().Where(i => i.JobId == jobId).OrderBy(i => i.LineIndex).ToListAsync(ct)
            : null;

        return TypedResults.Ok(JobResultView.From(job, items));
    }

    private static async Task<Results<Ok<FeedbackResponse>, NotFound, ValidationProblem>> FeedbackAsync(
        Guid transactionId, FeedbackRequest request, FeedbackService feedback, CancellationToken ct)
    {
        if (request.MerchantName is null && request.Category is null && request.Subcategory is null
            && request.PaymentMethod is null && request.IsRecurring is null)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { [""] = ["Provide at least one field to update."] });

        if (request.MerchantName is { } m && (string.IsNullOrWhiteSpace(m) || m.Length > 200))
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["merchantName"] = ["merchantName must have 1-200 characters."] });

        var result = await feedback.ApplyAsync(transactionId,
            new TransactionFeedback(request.MerchantName, request.Category, request.Subcategory, request.PaymentMethod, request.IsRecurring), ct);

        return result.Outcome switch
        {
            FeedbackOutcome.NotFound => TypedResults.NotFound(),
            FeedbackOutcome.Invalid => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["category"] = [result.Error!] }),
            _ => TypedResults.Ok(new FeedbackResponse(TransactionView.From(result.Item!), result.PromotedFullPattern)),
        };
    }

    private static Results<Accepted<JobAcceptedResponse>, Ok<JobAcceptedResponse>, ValidationProblem> ToAcceptedResult(IngestionResult result)
    {
        var url = $"/api/v1/reconciliations/{result.Job.Id}";
        var body = new JobAcceptedResponse(result.Job.Id, result.Job.Status, url, result.IsDuplicate);
        return result.IsDuplicate && result.Job.IsFinished
            ? TypedResults.Ok(body)
            : TypedResults.Accepted(url, body);
    }

    /// <summary>Lists what the client actually sent, e.g. "arquivo (File), externalReferenceId (Text)".</summary>
    private static string Describe(IFormCollection? form)
    {
        if (form is null) return "none";
        var fields = form.Files.Select(f => $"'{f.Name}' (File)").Concat(form.Keys.Select(k => $"'{k}' (Text)")).ToList();
        return fields.Count == 0 ? "none" : string.Join(", ", fields);
    }

    private static string? SerializeCategories(IReadOnlyList<CustomCategory>? categories) =>
        categories is { Count: > 0 } ? JsonSerializer.Serialize(categories, MycroftJson.Options) : null;

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
