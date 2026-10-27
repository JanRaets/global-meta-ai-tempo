using Tempo.Core.Domain;
using Tempo.Core.Geo;
using Tempo.Tests.Fakes;

namespace Tempo.Tests.CalendarSync;

/// <summary>13.a: geocoding is biased toward home, so short venue names resolve nearby.</summary>
public sealed class PlaceResolverFocusTests : IDisposable
{
    private readonly TestDb db = new();

    [Fact]
    public async Task Resolve_passes_home_as_the_focus_point()
    {
        var home = new Place { Id = Guid.NewGuid(), Name = "Thuis", Lat = 52.09, Lng = 5.23 };
        db.Context.AddRange(home, new UserSettings { Id = Guid.NewGuid(), LocationApiKey = "k", HomePlaceId = home.Id });
        db.Context.SaveChanges();
        var geocoder = new FakeGeocoder().Knows("Albert Heijn", 52.03, 5.17);

        await new PlaceResolver(db.Context, geocoder).ResolveAsync("Albert Heijn");

        var lookup = Assert.Single(geocoder.Lookups);
        Assert.Equal(new GeoPoint(home.Lat, home.Lng), lookup.Focus);
    }

    [Fact]
    public async Task Without_a_home_place_the_focus_is_null()
    {
        db.Context.Add(new UserSettings { Id = Guid.NewGuid(), LocationApiKey = "k" });
        db.Context.SaveChanges();
        var geocoder = new FakeGeocoder().Knows("Albert Heijn", 52.03, 5.17);

        await new PlaceResolver(db.Context, geocoder).ResolveAsync("Albert Heijn");

        Assert.Null(Assert.Single(geocoder.Lookups).Focus);
    }

    public void Dispose() => db.Dispose();
}
