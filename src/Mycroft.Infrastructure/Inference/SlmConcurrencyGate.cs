using Microsoft.Extensions.Options;

namespace Mycroft.Infrastructure.Inference;

/// <summary>Process-wide limiter for concurrent SLM calls (MAX_CONCURRENT_SLM_REQUESTS) to avoid OOM / CPU contention.</summary>
public sealed class SlmConcurrencyGate(IOptions<MycroftOptions> options) : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(Math.Max(1, options.Value.MaxConcurrentSlmRequests));

    public async ValueTask<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(_semaphore);
    }

    public void Dispose() => _semaphore.Dispose();

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) semaphore.Release();
        }
    }
}
