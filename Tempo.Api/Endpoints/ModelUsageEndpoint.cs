using FastEndpoints;
using Microsoft.Extensions.Options;
using Tempo.Core.Configuration;
using Tempo.Core.Models;
using Tempo.Core.Redis;

namespace Tempo.Api.Endpoints;

/// <summary>Per-provider/purpose call counts, token sums and latency — the source for the
/// README's honest usage numbers. 13.c adds per-provider totals, a configurable spend
/// estimate from token counts, and the routing cache hit/miss counters. Persisted since
/// 29 Oct 2026 (ModelCalls table): the numbers survive restarts; ?since=2026-10-27 narrows
/// the window. The routing counters remain in-memory.</summary>
public sealed class ModelUsageEndpoint(
    IModelCallLog log,
    RoutingCacheStats routingCache,
    Tempo.Core.Geo.TomTomDailyCounter tomTom,
    IOptions<ModelsOptions> models,
    IOptions<RoutingOptions> routing) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/admin/model-usage");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        DateTimeOffset? since = DateTimeOffset.TryParse(Query<string>("since", isRequired: false), out var s) ? s : null;
        var usage = log.Summarize(since);
        var o = models.Value;
        var byProvider = usage
            .GroupBy(u => u.Provider)
            .Select(g => new
            {
                provider = g.Key.ToString(),
                calls = g.Sum(u => u.Calls),
                failures = g.Sum(u => u.Failures),
                promptTokens = g.Sum(u => u.PromptTokens),
                completionTokens = g.Sum(u => u.CompletionTokens),
                estimatedUsd = Math.Round(
                    g.Sum(u => u.PromptTokens) / 1_000_000d * o.EstimatedUsdPer1MPromptTokens
                    + g.Sum(u => u.CompletionTokens) / 1_000_000d * o.EstimatedUsdPer1MCompletionTokens, 4),
            })
            .ToList();
        await Send.OkAsync(new
        {
            byProvider,
            usage,
            routingCache = new { hits = routingCache.Hits, misses = routingCache.Misses },
            // TomTom free tier is 2,500 routing calls/day; past the limit car falls back to ORS.
            routing = new { tomTomCallsToday = tomTom.Today, tomTomDailyLimit = routing.Value.TomTom.DailyLimit },
        }, ct);
    }
}
