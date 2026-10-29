using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tempo.Core.Data;
using Tempo.Core.Domain;
using Tempo.Core.Models;

namespace Tempo.Integrations.Meta;

/// <summary>
/// Persisted call accounting (29 Oct 2026, replacing the in-memory log in production): every
/// model call lands in the ModelCalls table, so /admin/model-usage — and the README's numbers —
/// survive restarts. A failed write is logged and swallowed: usage accounting must never fail
/// a model call.
/// </summary>
public sealed class DbModelCallLog(IServiceScopeFactory scopeFactory, ILogger<DbModelCallLog> logger) : IModelCallLog
{
    public void Record(ModelCallRecord record)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TempoDbContext>();
            db.ModelCalls.Add(new ModelCall
            {
                Id = Guid.NewGuid(),
                Provider = record.Provider,
                Model = record.Model,
                Purpose = record.Purpose,
                PromptTokens = record.PromptTokens,
                CompletionTokens = record.CompletionTokens,
                LatencyMs = record.LatencyMs,
                Success = record.Success,
                At = record.At,
            });
            db.SaveChanges();
        }
        catch (Exception ex)
        {
            logger.LogWarning("Model-call accounting write failed (call itself unaffected): {Message}", ex.Message);
        }
    }

    public IReadOnlyList<ModelUsageSummary> Summarize(DateTimeOffset? since = null)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TempoDbContext>();
        // Client-side filter and grouping: SQLite (tests) can't compare DateTimeOffset, and
        // the table grows by at most a few hundred rows a day.
        return db.ModelCalls.AsEnumerable()
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
}
