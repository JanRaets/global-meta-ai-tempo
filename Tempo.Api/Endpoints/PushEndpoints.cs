using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tempo.Core.Configuration;
using Tempo.Core.Data;
using Tempo.Core.Domain;

namespace Tempo.Api.Endpoints;

/// <summary>The browser's PushSubscription JSON, as `JSON.parse(JSON.stringify(sub))` sends it.</summary>
public sealed class PushSubscribeRequest
{
    public required string Endpoint { get; set; }
    public required PushKeys Keys { get; set; }
    public string? UserAgent { get; set; }

    public sealed class PushKeys
    {
        public required string P256dh { get; set; }
        public required string Auth { get; set; }
    }
}

/// <summary>Registers (or refreshes) this browser's push subscription (design D16).
/// Upsert by endpoint: re-subscribing from the same browser never duplicates rows.</summary>
public sealed class PushSubscribeEndpoint(TempoDbContext db) : Endpoint<PushSubscribeRequest>
{
    public override void Configure()
    {
        Post("/push/subscribe");
        AllowAnonymous();
    }

    public override async Task HandleAsync(PushSubscribeRequest req, CancellationToken ct)
    {
        var existing = await db.PushSubscriptions.SingleOrDefaultAsync(s => s.Endpoint == req.Endpoint, ct);
        if (existing is null)
        {
            db.PushSubscriptions.Add(new PushSubscription
            {
                Id = Guid.NewGuid(),
                Endpoint = req.Endpoint,
                P256dh = req.Keys.P256dh,
                Auth = req.Keys.Auth,
                UserAgent = req.UserAgent,
            });
        }
        else
        {
            existing.P256dh = req.Keys.P256dh;
            existing.Auth = req.Keys.Auth;
            existing.UserAgent = req.UserAgent;
        }
        await db.SaveChangesAsync(ct);
        await Send.OkAsync(new { subscribed = true }, ct);
    }
}

public sealed class PushUnsubscribeRequest
{
    public required string Endpoint { get; set; }
}

public sealed class PushUnsubscribeEndpoint(TempoDbContext db) : Endpoint<PushUnsubscribeRequest>
{
    public override void Configure()
    {
        Delete("/push/subscribe");
        AllowAnonymous();
    }

    public override async Task HandleAsync(PushUnsubscribeRequest req, CancellationToken ct)
    {
        await db.PushSubscriptions.Where(s => s.Endpoint == req.Endpoint).ExecuteDeleteAsync(ct);
        await Send.OkAsync(new { unsubscribed = true }, ct);
    }
}

/// <summary>The VAPID public key is public by definition — the browser needs it as
/// `applicationServerKey` to subscribe.</summary>
public sealed class VapidPublicKeyEndpoint(IOptions<PushOptions> options) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/push/vapid-public-key");
        AllowAnonymous();
    }

    public override Task HandleAsync(CancellationToken ct) =>
        Send.OkAsync(new { publicKey = options.Value.VapidPublicKey }, ct);
}
