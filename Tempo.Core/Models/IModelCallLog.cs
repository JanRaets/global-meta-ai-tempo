using Tempo.Core.Configuration;

namespace Tempo.Core.Models;

public sealed record ModelCallRecord(
    ModelProvider Provider,
    string Model,
    string Purpose,
    int? PromptTokens,
    int? CompletionTokens,
    long LatencyMs,
    bool Success,
    DateTimeOffset At);

public sealed record ModelUsageSummary(
    ModelProvider Provider,
    string Model,
    string Purpose,
    int Calls,
    int Failures,
    long PromptTokens,
    long CompletionTokens,
    long TotalLatencyMs);

/// <summary>Records every model call (provider, model, purpose, tokens, latency) so the README
/// can state usage honestly. Persisted to the ModelCalls table in production (29 Oct 2026);
/// <paramref name="since"/> limits a summary to calls at or after that moment.</summary>
public interface IModelCallLog
{
    void Record(ModelCallRecord record);
    IReadOnlyList<ModelUsageSummary> Summarize(DateTimeOffset? since = null);
}
