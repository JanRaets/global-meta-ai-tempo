using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Tempo.Core.Configuration;
using Tempo.Core.Domain;
using Tempo.Core.Location;
using Tempo.Core.Planning;
using Tempo.Tests.Fakes;

namespace Tempo.Tests.Planning;

/// <summary>Chained-origin amendment (30 Oct 2026, from real use: a 20:00 party was planned
/// from hockey ending 17:00 — "I always go home in between"): a look-ahead plan assumes home
/// unless the gap is shorter than the travel home plus Phase:StayThresholdMin, evidence from
/// the current fix, or a confirmed ReturnsHome/StaysPut habit says otherwise.</summary>
public sealed class ChainedOriginTests : IDisposable
{
    private static readonly DateTimeOffset Morning = DateTimeOffset.Parse("2026-10-30T07:00:00Z"); // Friday

    private readonly TestDb db = new();
    private readonly FakeTimeProvider clock = new(Morning);
    private readonly FakeLocationBuffer buffer = new();
    private readonly FakeRoutingClient routing = new();
    private readonly PlanOptionStore store = new();
    private readonly Place home;
    private readonly Place hockey;
    private readonly Place venue;
    private readonly Place office;

    public ChainedOriginTests()
    {
        home = new Place { Id = Guid.NewGuid(), Name = "Thuis", Lat = 52.0950, Lng = 5.2400 };
        hockey = new Place { Id = Guid.NewGuid(), Name = "Schaerweijde", Lat = 52.1042, Lng = 5.2658 };
        venue = new Place { Id = Guid.NewGuid(), Name = "Bij Jos", Lat = 52.0894, Lng = 5.1100 };
        office = new Place { Id = Guid.NewGuid(), Name = "Anatomiegebouw", Lat = 52.1000, Lng = 5.1342 };
        db.Context.AddRange(home, hockey, venue, office, new UserSettings
        {
            Id = Guid.NewGuid(),
            LocationApiKey = "k",
            HomePlaceId = home.Id,
            EnabledModes = [TravelMode.Car], // fake routing: car 12 min → heuristic threshold 57 min
        });
        db.Context.SaveChanges();
    }

    private Appointment Appointment(string title, Place place, string start, string end)
    {
        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            Title = title,
            Start = DateTimeOffset.Parse(start),
            End = DateTimeOffset.Parse(end),
            PlaceId = place.Id,
            Place = place,
            BufferMin = 5,
        };
        db.Context.Add(appointment);
        db.Context.SaveChanges();
        return appointment;
    }

    private PlanService Plans() => new(db.Context,
        new DeparturePlanner(routing, NullLogger<DeparturePlanner>.Instance),
        store, new CurrentLocationProvider(buffer, clock), clock, NullLogger<PlanService>.Instance,
        new PhaseOptions(), routing);

    private void FixAt(Place place) =>
        buffer.Points.Add(new LocationPoint(place.Lat, place.Lng, clock.GetUtcNow().AddMinutes(-1)));

    private Habit OriginHabit(HabitKind kind, Place place, ContextKey context)
    {
        var habit = new Habit
        {
            Id = Guid.NewGuid(),
            PlaceId = place.Id,
            Place = place,
            Kind = kind,
            State = HabitState.Confirmed,
            Context = context,
        };
        db.Context.Add(habit);
        db.Context.SaveChanges();
        return habit;
    }

    [Fact]
    public async Task A_three_hour_gap_after_hockey_plans_the_party_from_home()
    {
        Appointment("Hockey JO9", hockey, "2026-10-30T14:00:00Z", "2026-10-30T16:00:00Z");
        var party = Appointment("VERJAARDAGS FEESTJE VOOR JOS", venue, "2026-10-30T19:00:00Z", "2026-10-30T22:00:00Z");
        FixAt(home);

        var options = await Plans().OptionsAsync(party.Id);

        Assert.NotEmpty(options.Options);
        Assert.All(options.Options, o => Assert.Equal(OriginKind.Home, o.OriginKind));
    }

    [Fact]
    public async Task A_forty_minute_gap_still_chains()
    {
        var sync = Appointment("Weekly sync", office, "2026-10-30T08:00:00Z", "2026-10-30T09:00:00Z");
        var review = Appointment("Design review", venue, "2026-10-30T09:40:00Z", "2026-10-30T10:30:00Z");
        FixAt(home);

        var options = await Plans().OptionsAsync(review.Id);

        Assert.NotEmpty(options.Options);
        Assert.All(options.Options, o => Assert.Equal(OriginKind.PreviousAppointment, o.OriginKind));
        Assert.All(options.Options, o => Assert.Equal(sync.Title, o.OriginName));
    }

    [Fact]
    public async Task A_fix_at_home_after_the_previous_appointment_plans_from_home()
    {
        Appointment("Hockey JO9", hockey, "2026-10-30T14:00:00Z", "2026-10-30T16:00:00Z");
        var party = Appointment("VERJAARDAGS FEESTJE VOOR JOS", venue, "2026-10-30T19:00:00Z", "2026-10-30T22:00:00Z");
        clock.SetUtcNow(DateTimeOffset.Parse("2026-10-30T16:30:00Z")); // hockey over, user drove home
        FixAt(home);

        var options = await Plans().OptionsAsync(party.Id);

        Assert.NotEmpty(options.Options);
        Assert.All(options.Options, o => Assert.Equal(OriginKind.Home, o.OriginKind));
    }

    [Fact]
    public async Task A_fix_still_at_the_previous_place_after_its_end_chains_from_it()
    {
        var match = Appointment("Hockey JO9", hockey, "2026-10-30T14:00:00Z", "2026-10-30T16:00:00Z");
        var party = Appointment("VERJAARDAGS FEESTJE VOOR JOS", venue, "2026-10-30T19:00:00Z", "2026-10-30T22:00:00Z");
        clock.SetUtcNow(DateTimeOffset.Parse("2026-10-30T16:30:00Z")); // hockey over, still at the club
        FixAt(hockey);

        var options = await Plans().OptionsAsync(party.Id);

        Assert.NotEmpty(options.Options);
        Assert.All(options.Options, o => Assert.Equal(OriginKind.PreviousAppointment, o.OriginKind));
        Assert.All(options.Options, o => Assert.Equal(match.Title, o.OriginName));
    }

    [Fact]
    public async Task A_confirmed_StaysPut_habit_chains_a_two_hour_gap()
    {
        var match = Appointment("Hockey JO9", hockey, "2026-10-30T14:00:00Z", "2026-10-30T16:00:00Z");
        var dinner = Appointment("Team dinner", venue, "2026-10-30T18:00:00Z", "2026-10-30T20:00:00Z");
        FixAt(home);
        // Hockey ends 17:00 local on a Friday; the 2 h gap buckets as "medium".
        OriginHabit(HabitKind.StaysPut, hockey,
            new ContextKey(DayOfWeek.Friday, TimeBucket.Afternoon, TravelDirection.Other, "personal", "medium"));

        var options = await Plans().OptionsAsync(dinner.Id);

        Assert.NotEmpty(options.Options);
        Assert.All(options.Options, o => Assert.Equal(OriginKind.PreviousAppointment, o.OriginKind));
        Assert.All(options.Options, o => Assert.Equal(match.Title, o.OriginName));
    }

    [Fact]
    public async Task A_confirmed_ReturnsHome_habit_overrides_a_short_gap()
    {
        Appointment("Weekly sync", office, "2026-10-30T08:00:00Z", "2026-10-30T09:00:00Z");
        var review = Appointment("Design review", venue, "2026-10-30T09:40:00Z", "2026-10-30T10:30:00Z");
        FixAt(home);
        // No specific weekday: any Monday–Friday 40 min gap after a meeting.
        OriginHabit(HabitKind.ReturnsHome, home,
            new ContextKey(null, TimeBucket.Morning, TravelDirection.TowardHome, "meeting", "short"));

        var options = await Plans().OptionsAsync(review.Id);

        Assert.NotEmpty(options.Options);
        Assert.All(options.Options, o => Assert.Equal(OriginKind.Home, o.OriginKind));
    }

    [Fact]
    public async Task An_origin_change_defeats_the_jitter_guard()
    {
        Appointment("Weekly sync", office, "2026-10-30T08:00:00Z", "2026-10-30T09:00:00Z");
        var review = Appointment("Design review", venue, "2026-10-30T09:40:00Z", "2026-10-30T10:30:00Z");
        FixAt(home);
        var plans = Plans();
        var v1 = await plans.FallbackCommitAsync(review.Id);
        Assert.Equal(OriginKind.PreviousAppointment, v1!.OriginKind); // 40 min gap chains

        // A confirmed ReturnsHome habit flips the origin; the flat fake routing keeps the
        // times identical, which used to fall inside the ±3 min jitter guard.
        OriginHabit(HabitKind.ReturnsHome, home,
            new ContextKey(null, TimeBucket.Morning, TravelDirection.TowardHome, "meeting", "short"));
        var v2 = await plans.FallbackCommitAsync(review.Id);

        Assert.Equal(OriginKind.Home, v2!.OriginKind);
        Assert.Equal(v1.Version + 1, v2.Version);
    }

    public void Dispose() => db.Dispose();
}
