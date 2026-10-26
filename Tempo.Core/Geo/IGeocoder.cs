namespace Tempo.Core.Geo;

public sealed record GeocodeResult(double Lat, double Lng, string? Label);

public interface IGeocoder
{
    /// <summary>Null when the text cannot be geocoded.</summary>
    Task<GeocodeResult?> GeocodeAsync(string text, CancellationToken ct = default);
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
