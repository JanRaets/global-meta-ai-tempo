using FastEndpoints;
using Tempo.Core.Models;

namespace Tempo.Api.Endpoints;

/// <summary>Per-provider/purpose call counts, token sums and latency — the source for the README's honest usage numbers.</summary>
public sealed class ModelUsageEndpoint(IModelCallLog log) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/admin/model-usage");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct) =>
        await Send.OkAsync(log.Summarize(), ct);
}
