namespace Mycroft.Core.Enums;

/// <summary>Which layer of the pipeline produced the final result for a transaction.</summary>
public enum ResolutionSource
{
    AliasMatch,
    Cache,
    SlmBatch,
    SlmSingleRetry,
    RegexFallback
}
