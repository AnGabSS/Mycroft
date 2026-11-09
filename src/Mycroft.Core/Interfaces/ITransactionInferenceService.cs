using Mycroft.Core.Models;

namespace Mycroft.Core.Interfaces;

public interface ITransactionInferenceService
{
    /// <summary>
    /// Enriches the given items. Must return exactly one result per item (matched by LineIndex) and never throw
    /// for model/format errors: failures degrade to the regex fallback.
    /// </summary>
    Task<IReadOnlyList<ReconciledTransactionResult>> InferAsync(
        IReadOnlyList<InferenceRequestItem> items,
        InferenceContext context,
        CancellationToken cancellationToken = default);
}
