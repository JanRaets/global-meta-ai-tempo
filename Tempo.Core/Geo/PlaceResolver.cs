using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tempo.Core.Data;
using Tempo.Core.Domain;

namespace Tempo.Core.Geo;

/// <summary>Geocodes a location string to a Place, reusing an existing Place within 50 m
/// (calendar-sync spec: Location resolution). Caller saves changes. Resolution is ranked
/// (geocoding amendment, 28 Oct 2026): overrides first, then locality-constrained,
/// name-overlap-ranked candidates; every resolution is logged for the correction sheet.</summary>
public sealed class PlaceResolver(TempoDbContext db, IGeocoder geocoder, ILogger<PlaceResolver>? logger = null)
{
    private const double ReuseRadiusM = 50;
    private readonly ILogger log = logger ?? NullLogger<PlaceResolver>.Instance;

    public async Task<Guid?> ResolveAsync(string? rawLocation, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawLocation))
            return null;
        var raw = rawLocation.Trim();

        // A stored user correction beats every heuristic — geocoding was wrong here once.
        var overrides = await db.PlaceOverrides.AsNoTracking().ToListAsync(ct);
        if (overrides.FirstOrDefault(o => o.RawText.Equals(raw, StringComparison.OrdinalIgnoreCase)) is { } correction
            && await db.Places.AsNoTracking().AnyAsync(p => p.Id == correction.PlaceId, ct))
            return correction.PlaceId;

        var home = await HomeFocusAsync(ct);
        var primary = await geocoder.SearchAsync(raw, home, ct);
        if (primary.Count == 0)
            return null;

        var token = GeocodeRanker.LocalityToken(raw, primary);
        var shown = primary;
        var pool = token is null ? primary : primary.Where(c => GeocodeRanker.MatchesLocality(c, token)).ToList();
        string reason;
        if (token is null)
        {
            reason = "no locality in the text — ranked by name overlap, then distance to home";
        }
        else if (pool.Count > 0)
        {
            reason = $"locality '{token}' from the text constrained the candidates";
        }
        else
        {
            // (c) The home focus may have pushed every in-locality candidate out of the
            // top-10: re-query without it and apply the locality constraint again.
            var unfocused = await geocoder.SearchAsync(raw, null, ct);
            var retryPool = unfocused.Where(c => GeocodeRanker.MatchesLocality(c, token)).ToList();
            if (retryPool.Count > 0)
            {
                (shown, pool) = (unfocused, retryPool);
                reason = $"locality '{token}' matched only after dropping the home focus";
            }
            else
            {
                pool = primary; // (d) nothing satisfies the locality — nearest to home it is
                reason = $"no candidate in '{token}' anywhere — fell back to the nearest";
            }
        }

        var ranked = GeocodeRanker.Rank(raw, token, pool, home);
        var chosen = ranked[0];
        log.LogInformation("Geocode '{Raw}': chose '{Chosen}' ({Reason}); candidates: {Candidates}",
            raw, chosen.Label ?? chosen.Name, reason,
            string.Join(" | ", shown.Take(10).Select(c => $"{c.Name} [{c.Locality}]")));

        var place = await ResolvePointAsync(chosen.Lat, chosen.Lng, chosen.Label ?? raw, ct);
        await UpsertLogAsync(raw, ranked, reason, place.Id, ct);
        return place.Id;
    }

    /// <summary>Ranked candidates for the correction sheet's "search…" — no side effects.</summary>
    public async Task<IReadOnlyList<GeoCandidate>> SearchRankedAsync(string text, CancellationToken ct = default)
    {
        var home = await HomeFocusAsync(ct);
        var candidates = await geocoder.SearchAsync(text.Trim(), home, ct);
        var token = GeocodeRanker.LocalityToken(text, candidates);
        var pool = token is null ? candidates : candidates.Where(c => GeocodeRanker.MatchesLocality(c, token)).ToList();
        return GeocodeRanker.Rank(text, token, pool.Count > 0 ? pool : candidates, home);
    }

    private async Task UpsertLogAsync(string raw, IReadOnlyList<GeoCandidate> ranked, string reason, Guid chosenPlaceId, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(ranked.Take(10).Select(c => new
        {
            name = c.Name, label = c.Label, locality = c.Locality, lat = c.Lat, lng = c.Lng,
        }));
        var rows = await db.PlaceResolutionLogs.Where(l => l.RawText == raw).ToListAsync(ct);
        if (rows.FirstOrDefault() is { } row)
        {
            row.CandidatesJson = json;
            row.Reason = reason;
            row.ChosenPlaceId = chosenPlaceId;
            row.CreatedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            db.PlaceResolutionLogs.Add(new PlaceResolutionLog
            {
                Id = Guid.NewGuid(),
                RawText = raw,
                CandidatesJson = json,
                Reason = reason,
                ChosenPlaceId = chosenPlaceId,
            });
        }
    }

    /// <summary>Home as the Pelias focus point (13.a): short venue names from captures and
    /// calendar entries should resolve near the user, not to a same-named street abroad.</summary>
    public async Task<GeoPoint?> HomeFocusAsync(CancellationToken ct = default)
    {
        var homeId = await db.UserSettings.AsNoTracking().Select(s => s.HomePlaceId).FirstOrDefaultAsync(ct);
        if (homeId is null)
            return null;
        var home = await db.Places.AsNoTracking().SingleOrDefaultAsync(p => p.Id == homeId, ct);
        return home is null ? null : new GeoPoint(home.Lat, home.Lng);
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
