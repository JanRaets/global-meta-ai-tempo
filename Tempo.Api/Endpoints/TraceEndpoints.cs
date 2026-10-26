using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Tempo.Agent;
using Tempo.Core.Data;

namespace Tempo.Api.Endpoints;

/// <summary>The trace behind a nudge, rendered as plain-language steps (agent-loop spec).</summary>
public sealed class TraceByNudgeEndpoint(TempoDbContext db) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/traces/{nudgeId}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var nudgeId = Route<Guid>("nudgeId");
        var nudge = await db.Nudges.AsNoTracking().SingleOrDefaultAsync(n => n.Id == nudgeId, ct);
        if (nudge?.AgentTraceId is not { } traceId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var trace = await db.AgentTraces.AsNoTracking().SingleOrDefaultAsync(t => t.Id == traceId, ct);
        if (trace is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        await Send.OkAsync(new
        {
            steps = TraceRenderer.Render(trace),
            provider = trace.Provider?.ToString(),
            trigger = trace.Trigger,
            outcome = trace.Outcome.ToString(),
        }, ct);
    }
}
