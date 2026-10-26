using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Tempo.Core.Data;
using Tempo.Core.Location;

namespace Tempo.Api.Endpoints;

/// <summary>Per-user API key check: ?key= query param or Authorization header, raw or
/// "Bearer &lt;key&gt;" (Owntracks header support varies by client version).</summary>
public sealed class LocationApiKeyAuth(TempoDbContext db, IMemoryCache cache)
{
    public async Task<bool> IsValidAsync(HttpContext http, CancellationToken ct)
    {
        var presented = http.Request.Query["key"].FirstOrDefault()
            ?? http.Request.Headers.Authorization.FirstOrDefault()?.Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(presented))
            return false;
        var expected = await cache.GetOrCreateAsync("location-api-key", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            return await db.UserSettings.AsNoTracking().Select(s => s.LocationApiKey).FirstOrDefaultAsync(ct);
        });
        return expected is not null && presented == expected;
    }
}

public sealed class LocationPointRequest
{
    public double Lat { get; set; }
    public double Lng { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public double? AccuracyM { get; set; }
}

public sealed class LocationIngestEndpoint(LocationIngestService ingest, LocationApiKeyAuth auth) : Endpoint<LocationPointRequest>
{
    public override void Configure()
    {
        Post("/location");
        AllowAnonymous(); // guarded by the per-user API key below
    }

    public override async Task HandleAsync(LocationPointRequest req, CancellationToken ct)
    {
        if (!await auth.IsValidAsync(HttpContext, ct))
        {
            await Send.StringAsync("invalid api key", 401, cancellation: ct);
            return;
        }
        var result = await ingest.IngestAsync(new LocationPoint(req.Lat, req.Lng, req.Timestamp, req.AccuracyM), ct);
        if (!result.Accepted)
        {
            await Send.StringAsync(result.Reason!, 400, cancellation: ct);
            return;
        }
        await Send.StringAsync("", statusCode: 202, cancellation: ct); // 202 Accepted per spec
    }
}

/// <summary>Owntracks HTTP mode: always answer 200 with a JSON array ([]) once authenticated,
/// or Owntracks keeps the message queued forever. Invalid or stale points are dropped quietly.</summary>
public sealed class OwntracksEndpoint(LocationIngestService ingest, LocationApiKeyAuth auth, ILogger<OwntracksEndpoint> logger)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/location/owntracks");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!await auth.IsValidAsync(HttpContext, ct))
        {
            await Send.StringAsync("invalid api key", 401, cancellation: ct);
            return;
        }
        using var reader = new StreamReader(HttpContext.Request.Body);
        var body = await reader.ReadToEndAsync(ct);
        if (OwntracksMessage.TryMap(body) is { } point)
        {
            var result = await ingest.IngestAsync(point, ct);
            if (!result.Accepted)
                logger.LogInformation("Owntracks point dropped: {Reason}", result.Reason);
        }
        await Send.StringAsync("[]", contentType: "application/json", cancellation: ct);
    }
}

public sealed class LocationStatsEndpoint(ILocationBuffer buffer, CurrentLocationProvider current, TempoDbContext db, TimeProvider clock)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/admin/location-stats");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var lastHour = await buffer.GetSinceAsync(now.AddHours(-1), ct);
        var latest = await current.GetAsync(ct);
        await Send.OkAsync(new
        {
            pointsLastHour = lastHour.Count,
            lastPointAt = latest?.Point.Timestamp,
            lastPointAgeSeconds = latest is null ? null : (double?)Math.Round((now - latest.Point.Timestamp).TotalSeconds),
            isStale = latest?.IsStale,
            visitCount = await db.Visits.CountAsync(ct),
        }, ct);
    }
}

/// <summary>Dev-only: the key to paste into Owntracks / the PWA. 404 outside Development.</summary>
public sealed class LocationKeyEndpoint(TempoDbContext db, IWebHostEnvironment env) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/admin/location-key");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!env.IsDevelopment())
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var key = await db.UserSettings.AsNoTracking().Select(s => s.LocationApiKey).FirstAsync(ct);
        await Send.OkAsync(new
        {
            key,
            owntracksUrl = $"https://<tunnel-host>/location/owntracks?key={key}",
        }, ct);
    }
}
