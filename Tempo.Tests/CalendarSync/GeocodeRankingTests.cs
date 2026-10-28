using Microsoft.Extensions.Logging.Abstractions;
using Tempo.Core.Domain;
using Tempo.Core.Geo;
using Tempo.Tests.Fakes;

namespace Tempo.Tests.CalendarSync;

/// <summary>Geocoding amendment (28 Oct 2026): a locality named in the text constrains the
/// candidates, name overlap outranks distance to home, and a user correction beats both.
/// Candidate lists are the live Pelias responses recorded on 28 Oct 2026 (home focus
/// 52.07899, 5.25872 — Zeist; boundary NL).</summary>
public sealed class GeocodeRankingTests : IDisposable
{
    private static readonly GeoPoint Home = new(52.07899, 5.25872);

    // Recorded: "Kampong Hockey, Utrecht" — Pelias ranks the WRONG club (Zeist) first.
    private static readonly GeoCandidate[] KampongHockey =
    [
        new(52.104207, 5.265794, "Schaerweijde Hockey", "Schaerweijde Hockey, Zeist, UT, Netherlands", "Zeist"),
        new(52.075096, 5.150419, "Clubhuis Kampong", "Clubhuis Kampong, Utrecht, Netherlands", "Utrecht"),
        new(52.025286, 5.352317, "Doornse Hockey Club", "Doornse Hockey Club, Doorn, UT, Netherlands", "Doorn"),
        new(52.158114, 5.341794, "Amersfoortse Mixed Hockey Club", "Amersfoortse Mixed Hockey Club, Amersfoort, UT, Netherlands", "Amersfoort"),
        new(52.078043, 5.147219, "Hockey Republic", "Hockey Republic, Utrecht, Netherlands", "Utrecht"),
    ];

    // Recorded: "SV Kampong, Utrecht" — the right club is second behind Eerbeek.
    private static readonly GeoCandidate[] SvKampong =
    [
        new(52.103369, 6.048893, "SV Eerbeekse Boys", "SV Eerbeekse Boys, Eerbeek, GE, Netherlands", "Eerbeek"),
        new(52.074944, 5.150705, "SV Kampong", "SV Kampong, Utrecht, Netherlands", "Utrecht"),
        new(52.075096, 5.150419, "Clubhuis Kampong", "Clubhuis Kampong, Utrecht, Netherlands", "Utrecht"),
        new(52.076408, 5.152682, "Kampong", "Kampong, Utrecht, Netherlands", "Utrecht"),
        new(52.095752, 5.1254, "SvPO Utrecht", "SvPO Utrecht, Utrecht, Netherlands", "Utrecht"),
    ];

    // Recorded: "Albert Heijn" — no locality in the text; home bias is CORRECT here.
    private static readonly GeoCandidate[] AlbertHeijn =
    [
        new(52.026586, 5.16194, "Albert Heijn", "Albert Heijn, Houten, UT, Netherlands", "Houten"),
        new(52.376088, 4.906755, "Albert Heijn", "Albert Heijn, Amsterdam, NH, Netherlands", "Amsterdam"),
        new(52.034617, 5.344285, "Albert Heijn", "Albert Heijn, Doorn, UT, Netherlands", "Doorn"),
        new(52.086752, 5.240085, "Albert Heijn", "Albert Heijn, Zeist, UT, Netherlands", "Zeist"),
    ];

    private static readonly GeoCandidate[] Bekkerstraat =
    [
        new(52.100044, 5.134178, "Bekkerstraat 141", "Bekkerstraat 141, Utrecht, Netherlands", "Utrecht"),
    ];

    private readonly TestDb db = new();
    private readonly FakeGeocoder geocoder = new();

    public GeocodeRankingTests()
    {
        var home = new Place { Id = Guid.NewGuid(), Name = "Thuis", Lat = Home.Lat, Lng = Home.Lng };
        db.Context.Add(home);
        db.Context.Add(new UserSettings { Id = Guid.NewGuid(), LocationApiKey = "k", HomePlaceId = home.Id });
        db.Context.SaveChanges();
    }

    private PlaceResolver Resolver() => new(db.Context, geocoder, NullLogger<PlaceResolver>.Instance);

    private Place PlaceOf(Guid? id)
    {
        db.Context.SaveChanges(); // the resolver leaves saving to its caller
        return db.Context.Places.Single(p => p.Id == id);
    }

    [Fact]
    public async Task A_named_locality_beats_the_home_focus()
    {
        geocoder.KnowsCandidates("Kampong Hockey, Utrecht", KampongHockey);

        var placeId = await Resolver().ResolveAsync("Kampong Hockey, Utrecht");

        // Not Schaerweijde (Zeist, closest to home) — the text says Utrecht, and among the
        // Utrecht candidates "Kampong" in the name ties with "Hockey", distance decides.
        Assert.Contains("Kampong", PlaceOf(placeId).Name);
        var log = db.Context.PlaceResolutionLogs.Local.Single();
        Assert.Contains("locality 'Utrecht'", log.Reason);
    }

    [Fact]
    public async Task Name_token_overlap_outranks_distance_to_home()
    {
        geocoder.KnowsCandidates("SV Kampong, Utrecht", SvKampong);

        var placeId = await Resolver().ResolveAsync("SV Kampong, Utrecht");

        Assert.Equal("SV Kampong, Utrecht, Netherlands", PlaceOf(placeId).Name);
    }

    [Fact]
    public async Task Two_strings_for_the_same_venue_land_on_one_place()
    {
        geocoder.KnowsCandidates("Kampong Hockey, Utrecht", KampongHockey);
        geocoder.KnowsCandidates("SV Kampong, Utrecht", SvKampong);
        var resolver = Resolver();

        var first = await resolver.ResolveAsync("Kampong Hockey, Utrecht");
        await db.Context.SaveChangesAsync();
        var second = await resolver.ResolveAsync("SV Kampong, Utrecht");

        Assert.Equal(first, second); // Clubhuis and SV Kampong are ~26 m apart: 50 m reuse
    }

    [Fact]
    public async Task Without_a_locality_in_the_text_the_nearest_wins()
    {
        geocoder.KnowsCandidates("Albert Heijn", AlbertHeijn);

        var placeId = await Resolver().ResolveAsync("Albert Heijn");

        Assert.Equal("Albert Heijn, Zeist, UT, Netherlands", PlaceOf(placeId).Name); // ~1.3 km from home
    }

    [Fact]
    public async Task A_street_address_resolves_directly()
    {
        geocoder.KnowsCandidates("Bekkerstraat 141, Utrecht", Bekkerstraat);

        var placeId = await Resolver().ResolveAsync("Bekkerstraat 141, Utrecht");

        Assert.Equal("Bekkerstraat 141, Utrecht, Netherlands", PlaceOf(placeId).Name);
    }

    [Fact]
    public async Task No_locality_match_requeries_without_the_home_focus()
    {
        // Focused search: the home focus pushed every Amsterdam candidate out of the top-10.
        geocoder.KnowsCandidates("Cafe Luxembourg, Amsterdam",
            new GeoCandidate(52.09, 5.24, "Cafe Lux", "Cafe Lux, Zeist, UT, Netherlands", "Zeist"));
        geocoder.KnowsCandidatesUnfocused("Cafe Luxembourg, Amsterdam",
            new GeoCandidate(52.3645, 4.8900, "Cafe Luxembourg", "Cafe Luxembourg, Amsterdam, NH, Netherlands", "Amsterdam"));

        var placeId = await Resolver().ResolveAsync("Cafe Luxembourg, Amsterdam");

        Assert.Equal("Cafe Luxembourg, Amsterdam, NH, Netherlands", PlaceOf(placeId).Name);
        Assert.Contains("dropping the home focus", db.Context.PlaceResolutionLogs.Local.Single().Reason);
    }

    [Fact]
    public async Task An_override_beats_geocoding_entirely()
    {
        var corrected = new Place { Id = Guid.NewGuid(), Name = "SV Kampong", Lat = 52.0749, Lng = 5.1507 };
        db.Context.Add(corrected);
        db.Context.Add(new PlaceOverride { Id = Guid.NewGuid(), RawText = "Kampong Hockey, Utrecht", PlaceId = corrected.Id });
        db.Context.SaveChanges();
        // No candidates taught: a geocoder call would resolve nothing.

        var placeId = await Resolver().ResolveAsync("kampong hockey, utrecht"); // case-insensitive

        Assert.Equal(corrected.Id, placeId);
        Assert.Empty(geocoder.Lookups); // never even searched
    }

    public void Dispose() => db.Dispose();
}
