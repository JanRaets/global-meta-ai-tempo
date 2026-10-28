namespace Tempo.Core.Geo;

public sealed record GeocodeResult(double Lat, double Lng, string? Label);

/// <summary>One Pelias candidate (geocoding amendment, 28 Oct 2026).</summary>
public sealed record GeoCandidate(double Lat, double Lng, string? Name, string? Label, string? Locality);

public interface IGeocoder
{
    /// <summary>Null when the text cannot be geocoded. A focus point biases ranking toward
    /// nearby results (Pelias focus.point) — short venue names resolve wrongly without it.</summary>
    Task<GeocodeResult?> GeocodeAsync(string text, GeoPoint? focus = null, CancellationToken ct = default);

    /// <summary>The top candidates (up to ~10) for ranked resolution — PlaceResolver picks,
    /// not Pelias (28 Oct 2026: the home focus overrode an explicit city in the text).</summary>
    Task<IReadOnlyList<GeoCandidate>> SearchAsync(string text, GeoPoint? focus = null, CancellationToken ct = default);

    /// <summary>Label for a point (visit centroids); null when reverse geocoding fails.</summary>
    Task<string?> ReverseAsync(double lat, double lng, CancellationToken ct = default);
}

/// <summary>
/// Deterministic candidate ranking (geocoding amendment, 28 Oct 2026): a locality named in
/// the text constrains the candidates; name-token overlap with the text outranks distance
/// to home ("Kampong" in the name beats being 5 km closer); distance only breaks ties.
/// </summary>
public static class GeocodeRanker
{
    /// <summary>The locality the text itself claims: the segment after the last comma is
    /// always a claim (the requery without home focus verifies it even when the focused
    /// top-10 carries no such candidate); a bare word only counts when some candidate
    /// actually carries that locality — otherwise it cannot be told apart from the name.</summary>
    public static string? LocalityToken(string text, IReadOnlyList<GeoCandidate> candidates)
    {
        var segments = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 1 && segments[^1].Length > 1 && !segments[^1].Any(char.IsDigit))
            return segments[^1];
        var localities = candidates.Where(c => !string.IsNullOrWhiteSpace(c.Locality))
            .Select(c => c.Locality!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return Tokens(text).Select(w => localities.FirstOrDefault(l => l.Equals(w, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(l => l is not null);
    }

    public static bool MatchesLocality(GeoCandidate candidate, string locality) =>
        string.Equals(candidate.Locality, locality, StringComparison.OrdinalIgnoreCase);

    /// <summary>Orders a candidate pool: name-token overlap with the text first, distance
    /// to home second (ties keep Pelias order when home is unknown).</summary>
    public static IReadOnlyList<GeoCandidate> Rank(string text, string? localityToken, IReadOnlyList<GeoCandidate> pool, GeoPoint? home)
    {
        var textTokens = Tokens(text)
            .Where(t => localityToken is null || !t.Equals(localityToken, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return pool
            .OrderByDescending(c => Tokens(c.Name ?? c.Label ?? "").Count(textTokens.Contains))
            .ThenBy(c => home is null ? 0 : GeoMath.DistanceMeters(home.Lat, home.Lng, c.Lat, c.Lng))
            .ToList();
    }

    private static List<string> Tokens(string text) =>
        text.Split([' ', ',', '.', '-', '(', ')', '\''], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 1)
            .ToList();
}

public static class GeoMath
{
    private const double EarthRadiusM = 6_371_000;

    public static double DistanceMeters(double lat1, double lng1, double lat2, double lng2)
    {
        var dLat = Radians(lat2 - lat1);
        var dLng = Radians(lng2 - lng1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(Radians(lat1)) * Math.Cos(Radians(lat2)) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return 2 * EarthRadiusM * Math.Asin(Math.Sqrt(a));
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180;
}
