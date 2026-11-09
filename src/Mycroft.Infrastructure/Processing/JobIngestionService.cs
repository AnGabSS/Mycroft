using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mycroft.Core.Entities;
using Mycroft.Core.Enums;
using Mycroft.Core.Interfaces;
using Mycroft.Core.Models;
using Mycroft.Infrastructure.Persistence;

namespace Mycroft.Infrastructure.Processing;

public sealed record NewJob(
    SourceFormat Format,
    string PayloadHash,
    string? ExternalReferenceId,
    string? CustomCategoriesJson,
    string? CallbackUrl,
    string? WebhookSecret,
    string? OriginalFileName = null,
    byte[]? RawFile = null,
    IReadOnlyList<RawStatementLine>? Lines = null);

public sealed record IngestionResult(ReconciliationJob Job, bool IsDuplicate);

/// <summary>
/// Inbox: persists the job (and JSON lines) transactionally, then signals the in-memory queue.
/// Idempotent by ExternalReferenceId and by payload hash within the deduplication window.
/// </summary>
public sealed class JobIngestionService(
    MycroftDbContext db,
    IReconciliationJobQueue queue,
    IMerchantNormalizer normalizer,
    IOptions<MycroftOptions> options,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public async Task<IngestionResult> IngestAsync(NewJob request, CancellationToken ct)
    {
        var existing = await FindDuplicateAsync(request, ct);
        if (existing is { Status: JobStatus.Failed } && request.ExternalReferenceId is not null)
            return await RetryFailedAsync(existing.Id, request, ct);
        if (existing is not null) return new IngestionResult(existing, true);

        var job = new ReconciliationJob
        {
            ExternalReferenceId = request.ExternalReferenceId,
            PayloadHash = request.PayloadHash,
            SourceFormat = request.Format,
            OriginalFileName = request.OriginalFileName,
            RawFilePayload = request.RawFile,
            CustomCategoriesJson = request.CustomCategoriesJson,
            CallbackUrl = request.CallbackUrl,
            WebhookSecret = request.WebhookSecret,
            CreatedAt = _time.GetUtcNow(),
            WebhookStatus = WebhookStatus.NotConfigured,
        };

        db.ReconciliationJobs.Add(job);
        if (request.Lines is { } lines)
        {
            foreach (var line in lines)
                db.TransactionItems.Add(ReconciliationProcessor.CreateItem(job.Id, line, normalizer));
            job.TotalItems = lines.Count;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (request.ExternalReferenceId is not null)
        {
            // Concurrent request with the same ExternalReferenceId won the race (unique index).
            db.ChangeTracker.Clear();
            var winner = await FindDuplicateAsync(request, ct);
            if (winner is not null) return new IngestionResult(winner, true);
            throw;
        }

        await queue.EnqueueAsync(job.Id, ct);
        return new IngestionResult(job, false);
    }

    /// <summary>
    /// A failed job is not a final answer: resubmitting with the same ExternalReferenceId reprocesses it with the
    /// new payload, keeping the same jobId (the reference is unique, so a new job could never be created for it).
    /// </summary>
    private async Task<IngestionResult> RetryFailedAsync(Guid jobId, NewJob request, CancellationToken ct)
    {
        var job = await db.ReconciliationJobs.FirstAsync(j => j.Id == jobId, ct);
        db.TransactionItems.RemoveRange(await db.TransactionItems.Where(i => i.JobId == jobId).ToListAsync(ct));

        job.PayloadHash = request.PayloadHash;
        job.SourceFormat = request.Format;
        job.OriginalFileName = request.OriginalFileName;
        job.RawFilePayload = request.RawFile;
        job.CustomCategoriesJson = request.CustomCategoriesJson;
        job.CallbackUrl = request.CallbackUrl;
        job.WebhookSecret = request.WebhookSecret;
        job.Status = JobStatus.Pending;
        job.TotalItems = job.ProcessedItems = job.AliasHits = job.CacheHits = job.FallbackItems = 0;
        job.ErrorMessage = null;
        job.StartedAt = job.CompletedAt = null;
        job.CreatedAt = _time.GetUtcNow();
        job.WebhookStatus = WebhookStatus.NotConfigured;
        job.WebhookAttempts = 0;
        job.NextWebhookRetryAt = null;

        if (request.Lines is { } lines)
        {
            foreach (var line in lines)
                db.TransactionItems.Add(ReconciliationProcessor.CreateItem(job.Id, line, normalizer));
            job.TotalItems = lines.Count;
        }

        await db.SaveChangesAsync(ct);
        await queue.EnqueueAsync(job.Id, ct);
        return new IngestionResult(job, IsDuplicate: false);
    }

    private async Task<ReconciliationJob?> FindDuplicateAsync(NewJob request, CancellationToken ct)
    {
        // An explicit ExternalReferenceId is the idempotency key: a new id means the client wants a new job,
        // even for the same file (e.g. reprocessing after a parser fix). The payload hash only covers anonymous requests.
        if (request.ExternalReferenceId is not null)
        {
            return await db.ReconciliationJobs.AsNoTracking()
                .FirstOrDefaultAsync(j => j.ExternalReferenceId == request.ExternalReferenceId, ct);
        }

        var since = _time.GetUtcNow() - options.Value.DeduplicationWindow;
        return await db.ReconciliationJobs.AsNoTracking()
            .Where(j => j.PayloadHash == request.PayloadHash && j.CreatedAt >= since && j.Status != JobStatus.Failed)
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }
}
