using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tempo.Core.Configuration;
using Tempo.Core.Geo;

namespace Tempo.Integrations.Maps;

/// <summary>OpenRouteService Pelias geocoding (design D15). Directions and the Redis cache land in section 4.</summary>
public sealed class OrsGeocoder(HttpClient http, IOptions<RoutingOptions> options, ILogger<OrsGeocoder> logger) : IGeocoder
{
    public async Task<GeocodeResult?> GeocodeAsync(string text, CancellationToken ct = default)
    {
        if (options.Value.ApiKey is "")
            throw new InvalidOperationException("Missing configuration value Routing:ApiKey (OpenRouteService)");
        var response = await http.GetAsync($"geocode/search?text={Uri.EscapeDataString(text)}&size=1", ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogWarning("ORS geocode for '{Text}' failed ({Status}): {Body}", text, (int)response.StatusCode,
                body[..Math.Min(body.Length, 200)]);
            return null;
        }
        var parsed = await response.Content.ReadFromJsonAsync<WireGeocodeResponse>(cancellationToken: ct);
        var feature = parsed?.Features?.FirstOrDefault();
        if (feature?.Geometry?.Coordinates is not [var lng, var lat, ..])
            return null;
        return new GeocodeResult(lat, lng, feature.Properties?.Label);
    }

    public async Task<string?> ReverseAsync(double lat, double lng, CancellationToken ct = default)
    {
        if (options.Value.ApiKey is "")
            throw new InvalidOperationException("Missing configuration value Routing:ApiKey (OpenRouteService)");
        var response = await http.GetAsync(
            $"geocode/reverse?point.lat={lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
            $"&point.lon={lng.ToString(System.Globalization.CultureInfo.InvariantCulture)}&size=1", ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("ORS reverse geocode failed ({Status})", (int)response.StatusCode);
            return null;
        }
        var parsed = await response.Content.ReadFromJsonAsync<WireGeocodeResponse>(cancellationToken: ct);
        return parsed?.Features?.FirstOrDefault()?.Properties?.Label;
    }

    private sealed record WireGeocodeResponse([property: JsonPropertyName("features")] List<WireFeature>? Features);

    private sealed record WireFeature(
        [property: JsonPropertyName("geometry")] WireGeometry? Geometry,
        [property: JsonPropertyName("properties")] WireProperties? Properties);

    private sealed record WireGeometry([property: JsonPropertyName("coordinates")] double[]? Coordinates);

    private sealed record WireProperties([property: JsonPropertyName("label")] string? Label);
}
