namespace Mycroft.Infrastructure;

/// <summary>
/// Runtime settings. Bound from flat environment variables (see .env.example), e.g. MAX_ITEMS_PER_JOB.
/// </summary>
public sealed class MycroftOptions
{
    public string OllamaEndpoint { get; set; } = "http://localhost:11434";
    public string OllamaModel { get; set; } = "llama3.2:3b";
    public int MaxConcurrentSlmRequests { get; set; } = 1;
    /// <summary>Transactions per prompt. Small (3B) models lose track in long lists; 8 is a safe default.</summary>
    public int SlmChunkSize { get; set; } = 8;
    /// <summary>Per-call timeout. CPU-only inference of a full chunk can take several minutes.</summary>
    public TimeSpan SlmRequestTimeout { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>Sends a tiny request at startup so the first real job does not pay the model load time.</summary>
    public bool SlmWarmup { get; set; } = true;
    public decimal NeedsReviewThreshold { get; set; } = 0.70m;
    /// <summary>
    /// When true, SLM answers with confidence >= 0.90 in the default taxonomy are written to the global cache.
    /// Off by default: small models report high confidence even for wrong answers, and a cached mistake is then
    /// replayed for every future job without reaching the model. The cache is fed by human feedback instead.
    /// </summary>
    public bool SlmAutoLearn { get; set; }

    public int RetentionDays { get; set; } = 7;
    public int MaxUploadSizeMb { get; set; } = 10;
    public int MaxItemsPerJob { get; set; } = 2000;
    public int WorkerConcurrency { get; set; } = 1;
    public TimeSpan DeduplicationWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Used to sign webhooks of jobs that did not provide their own secret. Optional.</summary>
    public string? DefaultWebhookSecret { get; set; }
    public TimeSpan WebhookTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Delays between webhook retries (after the first immediate attempt).</summary>
    public IReadOnlyList<TimeSpan> WebhookRetrySchedule { get; set; } =
    [
        TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1)
    ];
}
