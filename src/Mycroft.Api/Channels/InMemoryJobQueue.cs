using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Mycroft.Core.Interfaces;

namespace Mycroft.Api.Channels;

/// <summary>
/// In-process job signal based on <see cref="Channel{T}"/>. Only ids travel through it: the job itself is
/// already durable in PostgreSQL, so losing the channel on restart is harmless (boot recovery re-enqueues).
/// </summary>
public sealed class InMemoryJobQueue : IReconciliationJobQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false,
    });

    public ValueTask EnqueueAsync(Guid jobId, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(jobId, cancellationToken);

    public async IAsyncEnumerable<Guid> DequeueAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var id in _channel.Reader.ReadAllAsync(cancellationToken))
            yield return id;
    }
}
