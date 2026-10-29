using System.Collections.Concurrent;
using Tempo.Core.Models;

namespace Tempo.Integrations.Meta;

/// <summary>In-memory call accounting — the test default; production uses the persisted
/// <see cref="DbModelCallLog"/> (29 Oct 2026).</summary>
public sealed class InMemoryModelCallLog : IModelCallLog
{
    private readonly ConcurrentQueue<ModelCallRecord> records = new();

    public void Record(ModelCallRecord record) => records.Enqueue(record);

    public IReadOnlyList<ModelUsageSummary> Summarize(DateTimeOffset? since = null) =>
        records
            .Where(r => since is null || r.At >= since)
            .GroupBy(r => (r.Provider, r.Model, r.Purpose))
            .Select(g => new ModelUsageSummary(
                g.Key.Provider,
                g.Key.Model,
                g.Key.Purpose,
                g.Count(),
                g.Count(r => !r.Success),
                g.Sum(r => (long)(r.PromptTokens ?? 0)),
                g.Sum(r => (long)(r.CompletionTokens ?? 0)),
                g.Sum(r => r.LatencyMs)))
            .OrderBy(s => s.Provider).ThenBy(s => s.Purpose)
            .ToList();
}
