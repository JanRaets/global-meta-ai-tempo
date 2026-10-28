using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Tempo.Core.Configuration;
using Tempo.Core.Domain;
using Tempo.Core.Location;
using Tempo.Core.Nudging;
using Tempo.Core.Planning;
using Tempo.Tests.Fakes;
using Tempo.Tests.Nudging;

namespace Tempo.Tests.Planning;

/// <summary>D6 amendment (28 Oct 2026): no travel when already at the destination, and
/// chained origins for look-ahead plans — two meetings at the same office must not produce
/// a travel plan, a coffee stop and a leave nudge for the second one.</summary>
public sealed class AtDestinationTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-27T07:00:00Z"); // Tuesday

    private readonly TestDb db = new();
    private readonly FakeTimeProvider clock = new(Now);
    private readonly FakeLocationBuffer buffer = new();
    private readonly FakeRoutingClient routing = new();
    private readonly PlanOptionStore store = new();
    private readonly Place home;
    private readonly Place office;
    private readonly Place dentist;

    public AtDestinationTests()
    {
        home = new Place { Id = Guid.NewGuid(), Name = "Thuis", Lat = 52.0950, Lng = 5.2400 };
        office = new Place { Id = Guid.NewGuid(), Name = "Anatomiegebouw", Lat = 52.1000, Lng = 5.1342 };
        dentist = new Place { Id = Guid.NewGuid(), Name = "Dentist", Lat = 52.0894, Lng = 5.1100 };
        db.Context.AddRange(home, office, dentist, new UserSettings
        {
            Id = Guid.NewGuid(),
            LocationApiKey = "k",
            HomePlaceId = home.Id,
            EnabledModes = [TravelMode.Bicycle],
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
        new PhaseOptions());

    private PlanPhaseService Phases() => new(db.Context,
        new CurrentLocationProvider(buffer, clock), buffer, new PhaseOptions(), clock, NullLogger<PlanPhaseService>.Instance);

    private void FixAt(double lat, double lng) =>
        buffer.Points.Add(new LocationPoint(lat, lng, clock.GetUtcNow().AddMinutes(-1)));

    [Fact]
    public async Task Already_there_yields_one_no_travel_option_and_no_stops()
    {
        // A confirmed habit that would otherwise be matched into stop subsets.
        db.Context.AddRange(new Habit
        {
            Id = Guid.NewGuid(),
            PlaceId = dentist.Id,
            Place = dentist,
            State = HabitState.Confirmed,
            AvgDurationMin = 8,
            VisitCount = 4,
            Context = new ContextKey(DayOfWeek.Tuesday, TimeBucket.Morning, TravelDirection.Other, null),
        });
        db.Context.SaveChanges();
        var meeting = Appointment("Weekly Alignment", office, "2026-10-27T09:30:00Z", "2026-10-27T10:30:00Z");
        FixAt(office.Lat, office.Lng); // already at the office

        var result = await Plans().OptionsAsync(meeting.Id);

        var option = Assert.Single(result.Options);
        Assert.True(option.IsNoTravel);
        Assert.Empty(option.Stops);
        Assert.Equal(meeting.Start, option.LeaveAt);
        Assert.Equal("no travel — already at the destination", result.Note);
    }

    [Fact]
    public async Task Already_there_is_AtDestination_and_every_nudge_is_refused()
    {
        var meeting = Appointment("Weekly Alignment", office, "2026-10-27T09:30:00Z", "2026-10-27T10:30:00Z");
        FixAt(office.Lat, office.Lng);
        var plans = Plans();
        var options = await plans.OptionsAsync(meeting.Id);
        await plans.CommitAsync(options.Options[0].OptionId, "already at destination", PlanCommitter.Fallback);

        var transition = Assert.Single(await Phases().EvaluateAllAsync());
        Assert.Equal(PlanPhase.AtDestination, transition.To);

        var nudges = new NudgeService(db.Context, store, new RecordingNudgeTransport(), clock, NullLogger<NudgeService>.Instance);
        var leave = await nudges.SendAsync(NudgeKind.Leave, "Leave at 09:30 by walking.", meeting.Id, null);
        Assert.False(leave.Sent);
        Assert.Contains("already at the destination", leave.RefusalReason);
        var late = await nudges.SendAsync(NudgeKind.Late, "Leave now and arrive by 10:30.", meeting.Id, null);
        Assert.False(late.Sent);
    }

    [Fact]
    public async Task Moving_away_before_start_reverts_to_upcoming()
    {
        var meeting = Appointment("Weekly Alignment", office, "2026-10-27T09:30:00Z", "2026-10-27T10:30:00Z");
        FixAt(office.Lat, office.Lng);
        var plans = Plans();
        var options = await plans.OptionsAsync(meeting.Id);
        await plans.CommitAsync(options.Options[0].OptionId, "already at destination", PlanCommitter.Fallback);
        await Phases().EvaluateAllAsync(); // → AtDestination

        clock.SetUtcNow(Now.AddMinutes(30));
        FixAt(office.Lat, office.Lng + 0.004); // ~270 m away, 2 h before start

        var transition = Assert.Single(await Phases().EvaluateAllAsync());
        Assert.Equal((PlanPhase.AtDestination, PlanPhase.Upcoming), (transition.From, transition.To));
    }

    [Fact]
    public async Task Office_office_dentist_produces_one_departure_each_and_a_no_travel_middle()
    {
        var first = Appointment("Bijpraten", office, "2026-10-27T08:00:00Z", "2026-10-27T09:00:00Z");
        var second = Appointment("MT Weekly", office, "2026-10-27T09:00:00Z", "2026-10-27T10:00:00Z");
        var third = Appointment("Dentist visit", dentist, "2026-10-27T10:30:00Z", "2026-10-27T11:00:00Z");
        FixAt(home.Lat, home.Lng); // at home, before the day starts
        var plans = Plans();

        var firstOptions = await plans.OptionsAsync(first.Id);
        Assert.All(firstOptions.Options, o => Assert.False(o.IsNoTravel)); // home → office travels
        Assert.All(firstOptions.Options, o => Assert.Equal(OriginKind.Current, o.OriginKind));

        var secondOptions = await plans.OptionsAsync(second.Id);
        var middle = Assert.Single(secondOptions.Options); // same office: no travel
        Assert.True(middle.IsNoTravel);
        Assert.Equal(OriginKind.PreviousAppointment, middle.OriginKind);
        Assert.Equal("Bijpraten", middle.OriginName);

        var thirdOptions = await plans.OptionsAsync(third.Id);
        Assert.All(thirdOptions.Options, o => Assert.False(o.IsNoTravel)); // office → dentist travels
        Assert.All(thirdOptions.Options, o => Assert.Equal(OriginKind.PreviousAppointment, o.OriginKind));
        Assert.All(thirdOptions.Options, o => Assert.Equal("MT Weekly", o.OriginName));
    }

    [Fact]
    public async Task An_overlapping_previous_appointment_is_the_chain_origin()
    {
        // Live 28 Oct: pickup 14:00–15:00, JO7 15:30–16:30 at Schaerweijde, JO9 16:00 at
        // Kampong — JO9 must chain from JO7 (where the user is), not from the pickup.
        var schaerweijde = new Place { Id = Guid.NewGuid(), Name = "Schaerweijde", Lat = 52.1042, Lng = 5.2658 };
        db.Context.Add(schaerweijde);
        db.Context.SaveChanges();
        Appointment("Pickup kids", office, "2026-10-27T13:00:00Z", "2026-10-27T14:00:00Z");
        var jo7 = Appointment("JO7-03", schaerweijde, "2026-10-27T14:30:00Z", "2026-10-27T15:30:00Z");
        var jo9 = Appointment("JO9-03", dentist, "2026-10-27T15:00:00Z", "2026-10-27T16:00:00Z");
        FixAt(home.Lat, home.Lng);

        var options = await Plans().OptionsAsync(jo9.Id);

        Assert.NotEmpty(options.Options);
        Assert.All(options.Options, o => Assert.Equal(OriginKind.PreviousAppointment, o.OriginKind));
        Assert.All(options.Options, o => Assert.Equal(jo7.Title, o.OriginName));
    }

    [Fact]
    public async Task A_chain_gap_beyond_three_hours_falls_back_to_home()
    {
        Appointment("Morning sync", office, "2026-10-27T07:30:00Z", "2026-10-27T08:00:00Z");
        var evening = Appointment("Dentist visit", dentist, "2026-10-27T12:30:00Z", "2026-10-27T13:00:00Z");
        FixAt(home.Lat, home.Lng);

        var options = await Plans().OptionsAsync(evening.Id);

        Assert.NotEmpty(options.Options);
        Assert.All(options.Options, o => Assert.Equal(OriginKind.Home, o.OriginKind));
    }

    public void Dispose() => db.Dispose();
}
