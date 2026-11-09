namespace Mycroft.Core.Interfaces;

/// <summary>Fast in-process signal that a persisted job is ready. The database remains the source of truth.</summary>
public interface IReconciliationJobQueue
{
    ValueTask EnqueueAsync(Guid jobId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken cancellationToken);
}
