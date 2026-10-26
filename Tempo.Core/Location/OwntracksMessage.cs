using System.Text.Json;

namespace Tempo.Core.Location;

/// <summary>Maps Owntracks HTTP-mode payloads to the standard point (location-ingest spec:
/// Owntracks ingestion). Only `_type:"location"` carries a point; everything else
/// (lwt, transition, status, …) is acknowledged and ignored.</summary>
public static class OwntracksMessage
{
    public static LocationPoint? TryMap(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("_type", out var type) || type.GetString() != "location"
                || !root.TryGetProperty("lat", out var lat) || !root.TryGetProperty("lon", out var lon)
                || !root.TryGetProperty("tst", out var tst))
                return null;
            double? accuracy = root.TryGetProperty("acc", out var acc) && acc.ValueKind == JsonValueKind.Number
                ? acc.GetDouble()
                : null;
            return new LocationPoint(
                lat.GetDouble(),
                lon.GetDouble(),
                DateTimeOffset.FromUnixTimeSeconds(tst.GetInt64()),
                accuracy);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }
}
