using FastEndpoints;
using Microsoft.Extensions.Options;
using Tempo.Core.Configuration;
using Tempo.Core.Models;
using Tempo.Core.Redis;

namespace Tempo.Api.Endpoints;

/// <summary>Per-provider/purpose call counts, token sums and latency — the source for the
/// README's honest usage numbers. 13.c adds per-provider totals, a configurable spend
/// estimate from token counts, and the routing cache hit/miss counters.</summary>
public sealed class ModelUsageEndpoint(
    IModelCallLog log,
    RoutingCacheStats routingCache,
    IOptions<ModelsOptions> models) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/admin/model-usage");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var usage = log.Summarize();
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
        }, ct);
    }
}
