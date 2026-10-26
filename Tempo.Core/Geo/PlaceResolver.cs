using Microsoft.EntityFrameworkCore;
using Tempo.Core.Data;
using Tempo.Core.Domain;

namespace Tempo.Core.Geo;

/// <summary>Geocodes a location string to a Place, reusing an existing Place within 50 m
/// (calendar-sync spec: Location resolution). Caller saves changes.</summary>
public sealed class PlaceResolver(TempoDbContext db, IGeocoder geocoder)
{
    private const double ReuseRadiusM = 50;

    public async Task<Guid?> ResolveAsync(string? rawLocation, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawLocation))
            return null;
        var geo = await geocoder.GeocodeAsync(rawLocation, ct);
        if (geo is null)
            return null;
        return (await ResolvePointAsync(geo.Lat, geo.Lng, geo.Label ?? rawLocation, ct)).Id;
    }

    /// <summary>Point → Place with the same 50 m reuse; also the path visits take (section 5).</summary>
    public async Task<Place> ResolvePointAsync(double lat, double lng, string fallbackName, CancellationToken ct = default)
    {
        // Single user: the places table stays small, a scan is fine. Load into the change
        // tracker and match against Local so places added earlier in the same unit of work
        // (one sync run) are reused too, not duplicated.
        await db.Places.LoadAsync(ct);
        var near = db.Places.Local
            .Select(p => (Place: p, Distance: GeoMath.DistanceMeters(p.Lat, p.Lng, lat, lng)))
            .Where(x => x.Distance <= ReuseRadiusM)
            .OrderBy(x => x.Distance)
            .Select(x => x.Place)
            .FirstOrDefault();
        if (near is not null)
            return near;
        var place = new Place { Id = Guid.NewGuid(), Name = fallbackName, Lat = lat, Lng = lng };
        db.Places.Add(place);
        return place;
    }
}
