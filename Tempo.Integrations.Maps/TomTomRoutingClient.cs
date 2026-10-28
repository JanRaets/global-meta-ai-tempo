using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tempo.Core.Configuration;
using Tempo.Core.Domain;
using Tempo.Core.Geo;

namespace Tempo.Integrations.Maps;

/// <summary>
/// TomTom Routing for traffic-aware car estimates (added 28 Oct 2026): GET
/// routing/1/calculateRoute/{lat,lon}:…:{lat,lon}/json with traffic=true and departAt.
/// travelTimeInSeconds already includes the live delay; trafficDelayInSeconds is the
/// delay share, surfaced as RouteEstimate.TrafficDelay. Car only — every other mode (and
/// any failure, missing key or spent daily quota) is a RoutingUnavailableException that
/// the composite answers with ORS. Cached like ORS (same rounding, same TTL; delay in a
/// sibling key) so the tick stays inside the 2,500/day free tier, guarded by the counter.
/// </summary>
public sealed class TomTomRoutingClient(
    HttpClient http,
    IOptions<RoutingOptions> options,
    IRoutingCache cache,
    TomTomDailyCounter counter,
    TimeProvider clock,
    ILogger<TomTomRoutingClient> logger) : IRoutingClient
{
    public async Task<RouteEstimate> EstimateAsync(GeoPoint from, GeoPoint to, TravelMode mode, DateTimeOffset departAt, CancellationToken ct = default)
    {
        var (duration, delay) = await RouteAsync([from, to], mode, departAt, ct);
        return new RouteEstimate(duration, delay);
    }

    public async Task<TimeSpan> DetourAsync(GeoPoint from, IReadOnlyList<GeoPoint> via, GeoPoint to, TravelMode mode, CancellationToken ct = default)
    {
        var (duration, _) = await RouteAsync([from, .. via, to], mode, departAt: null, ct);
        return duration;
    }

    private async Task<(TimeSpan Duration, TimeSpan Delay)> RouteAsync(
        IReadOnlyList<GeoPoint> points, TravelMode mode, DateTimeOffset? departAt, CancellationToken ct)
    {
        if (mode != TravelMode.Car)
            throw new RoutingUnavailableException($"TomTom routes car only, not {mode}");
        var o = options.Value.TomTom;
        if (o.ApiKey is "")
            throw new RoutingUnavailableException("TomTom not configured (Routing:TomTom:ApiKey)");

        var bucket = departAt is { } d ? DepartBucket(d) : null;
        var key = CacheKey(points, bucket);
        if (await cache.GetSecondsAsync(key, ct) is { } cachedSeconds)
        {
            var cachedDelay = await cache.GetSecondsAsync(key + ":delay", ct) ?? 0;
            return (TimeSpan.FromSeconds(cachedSeconds), TimeSpan.FromSeconds(cachedDelay));
        }

        if (!counter.TryTake(o.DailyLimit))
            throw new RoutingUnavailableException($"TomTom daily limit ({o.DailyLimit}) spent");

        var locations = string.Join(":", points.Select(p =>
            $"{p.Lat.ToString("F5", CultureInfo.InvariantCulture)},{p.Lng.ToString("F5", CultureInfo.InvariantCulture)}"));
        // departAt must lie in the future for TomTom; "now" is the default when omitted.
        var depart = departAt is { } at && at > clock.GetUtcNow().AddMinutes(1)
            ? $"&departAt={Uri.EscapeDataString(at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"))}"
            : "";
        var url = $"routing/1/calculateRoute/{locations}/json?traffic=true&travelMode=car{depart}&key={Uri.EscapeDataString(o.ApiKey)}";

        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(url, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new RoutingUnavailableException($"TomTom unreachable: {ex.Message}", ex);
        }
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new RoutingUnavailableException(
                $"TomTom routing returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 300)]}");
        }

        var parsed = await response.Content.ReadFromJsonAsync<WireRouteResponse>(cancellationToken: ct);
        var summary = parsed?.Routes?.FirstOrDefault()?.Summary
            ?? throw new RoutingUnavailableException("TomTom routing returned no route");
        var duration = summary.TravelTimeInSeconds
            ?? throw new RoutingUnavailableException("TomTom routing returned no travel time");
        var delay = summary.TrafficDelayInSeconds ?? 0;
        await cache.SetSecondsAsync(key, duration, TimeSpan.FromMinutes(options.Value.CacheMinutes), ct);
        await cache.SetSecondsAsync(key + ":delay", delay, TimeSpan.FromMinutes(options.Value.CacheMinutes), ct);
        logger.LogInformation("TomTom car route: {Duration:F0}s (+{Delay:F0}s traffic) over {Points} points",
            duration, delay, points.Count);
        return (TimeSpan.FromSeconds(duration), TimeSpan.FromSeconds(delay));
    }

    private static string CacheKey(IReadOnlyList<GeoPoint> points, string? departBucket)
    {
        var coords = string.Join(";", points.Select(p =>
            $"{Math.Round(p.Lat, 3).ToString(CultureInfo.InvariantCulture)},{Math.Round(p.Lng, 3).ToString(CultureInfo.InvariantCulture)}"));
        return $"route:tomtom-car:{coords}:{departBucket ?? "-"}";
    }

    private static string DepartBucket(DateTimeOffset departAt) =>
        (departAt.ToUnixTimeSeconds() / 300).ToString(CultureInfo.InvariantCulture);

    private sealed record WireRouteResponse([property: JsonPropertyName("routes")] List<WireRoute>? Routes);

    private sealed record WireRoute([property: JsonPropertyName("summary")] WireSummary? Summary);

    private sealed record WireSummary(
        [property: JsonPropertyName("travelTimeInSeconds")] double? TravelTimeInSeconds,
        [property: JsonPropertyName("trafficDelayInSeconds")] double? TrafficDelayInSeconds);
}

/// <summary>Car goes to TomTom for live traffic (Routing:Car:Provider, default TomTom) with
/// ORS as the in-flight fallback; bicycle and walking stay on ORS. The straight-line D18
/// fallback sits above this, in the planner, and is unchanged.</summary>
public sealed class CompositeRoutingClient(
    TomTomRoutingClient tomtom,
    OrsRoutingClient ors,
    IOptions<RoutingOptions> options,
    ILogger<CompositeRoutingClient> logger) : IRoutingClient
{
    private bool CarViaTomTom =>
        string.Equals(options.Value.Car.Provider, "TomTom", StringComparison.OrdinalIgnoreCase);

    public async Task<RouteEstimate> EstimateAsync(GeoPoint from, GeoPoint to, TravelMode mode, DateTimeOffset departAt, CancellationToken ct = default)
    {
        if (mode == TravelMode.Car && CarViaTomTom)
        {
            try
            {
                return await tomtom.EstimateAsync(from, to, mode, departAt, ct);
            }
            catch (RoutingUnavailableException ex)
            {
                logger.LogWarning("TomTom estimate failed, falling back to ORS: {Message}", ex.Message);
            }
        }
        return await ors.EstimateAsync(from, to, mode, departAt, ct);
    }

    public async Task<TimeSpan> DetourAsync(GeoPoint from, IReadOnlyList<GeoPoint> via, GeoPoint to, TravelMode mode, CancellationToken ct = default)
    {
        if (mode == TravelMode.Car && CarViaTomTom)
        {
            try
            {
                return await tomtom.DetourAsync(from, via, to, mode, ct);
            }
            catch (RoutingUnavailableException ex)
            {
                logger.LogWarning("TomTom detour failed, falling back to ORS: {Message}", ex.Message);
            }
        }
        return await ors.DetourAsync(from, via, to, mode, ct);
    }
}
