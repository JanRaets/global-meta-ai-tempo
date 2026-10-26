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
/// OpenRouteService Directions (design D15): POST /v2/directions/{profile} with
/// [[lon,lat],…] — longitude first. Results are cached for Routing:CacheMinutes keyed on
/// rounded inputs (3 decimals ≈ 100 m, 5-minute departAt bucket) to stay inside the free
/// quota (~40/min, 2000/day). Any 429/5xx/network problem surfaces as
/// RoutingUnavailableException so the planner can fall back deterministically.
/// </summary>
public sealed class OrsRoutingClient(
    HttpClient http,
    IOptions<RoutingOptions> options,
    IRoutingCache cache,
    ILogger<OrsRoutingClient> logger) : IRoutingClient
{
    public Task<TimeSpan> EstimateAsync(GeoPoint from, GeoPoint to, TravelMode mode, DateTimeOffset departAt, CancellationToken ct = default) =>
        RouteAsync([from, to], mode, DepartBucket(departAt), ct);

    public Task<TimeSpan> DetourAsync(GeoPoint from, IReadOnlyList<GeoPoint> via, GeoPoint to, TravelMode mode, CancellationToken ct = default) =>
        RouteAsync([from, .. via, to], mode, departBucket: null, ct);

    private async Task<TimeSpan> RouteAsync(IReadOnlyList<GeoPoint> points, TravelMode mode, string? departBucket, CancellationToken ct)
    {
        if (options.Value.ApiKey is "")
            throw new InvalidOperationException("Missing configuration value Routing:ApiKey (OpenRouteService)");
        var profile = ProfileOf(mode);
        var key = CacheKey(profile, points, departBucket);
        if (await cache.GetSecondsAsync(key, ct) is { } cachedSeconds)
            return TimeSpan.FromSeconds(cachedSeconds);

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync($"v2/directions/{profile}",
                new WireDirectionsRequest(points.Select(p => new[] { p.Lng, p.Lat }).ToArray()), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new RoutingUnavailableException($"ORS unreachable: {ex.Message}", ex);
        }
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new RoutingUnavailableException(
                $"ORS directions ({profile}) returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 300)]}");
        }

        var parsed = await response.Content.ReadFromJsonAsync<WireDirectionsResponse>(cancellationToken: ct);
        var duration = parsed?.Routes?.FirstOrDefault()?.Summary?.Duration
            ?? throw new RoutingUnavailableException("ORS directions returned no route");
        await cache.SetSecondsAsync(key, duration, TimeSpan.FromMinutes(options.Value.CacheMinutes), ct);
        logger.LogInformation("ORS {Profile} route: {Duration:F0}s over {Points} points", profile, duration, points.Count);
        return TimeSpan.FromSeconds(duration);
    }

    private static string ProfileOf(TravelMode mode) => mode switch
    {
        TravelMode.Walking => "foot-walking",
        TravelMode.Bicycle => "cycling-regular",
        TravelMode.Car => "driving-car",
        _ => throw new RoutingUnavailableException($"No ORS profile for mode {mode} (train is not routable; design D15)"),
    };

    private static string CacheKey(string profile, IReadOnlyList<GeoPoint> points, string? departBucket)
    {
        var coords = string.Join(";", points.Select(p =>
            $"{Math.Round(p.Lat, 3).ToString(CultureInfo.InvariantCulture)},{Math.Round(p.Lng, 3).ToString(CultureInfo.InvariantCulture)}"));
        return $"route:{profile}:{coords}:{departBucket ?? "-"}";
    }

    private static string DepartBucket(DateTimeOffset departAt) =>
        (departAt.ToUnixTimeSeconds() / 300).ToString(CultureInfo.InvariantCulture);

    private sealed record WireDirectionsRequest([property: JsonPropertyName("coordinates")] double[][] Coordinates);

    private sealed record WireDirectionsResponse([property: JsonPropertyName("routes")] List<WireRoute>? Routes);

    private sealed record WireRoute([property: JsonPropertyName("summary")] WireSummary? Summary);

    private sealed record WireSummary(
        [property: JsonPropertyName("duration")] double? Duration,
        [property: JsonPropertyName("distance")] double? Distance);
}
