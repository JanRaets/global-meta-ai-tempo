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

/// <summary>13.e / design D6 phase model: every transition, the no-nudge-once-moving rule,
/// and the deterministic in-place refresh of a late plan.</summary>
public sealed class PlanPhaseTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-27T08:00:00Z");

    private readonly TestDb db = new();
    private readonly FakeTimeProvider clock = new(Now);
    private readonly FakeLocationBuffer buffer = new();
    private readonly FakeRoutingClient routing = new();
    private readonly PlanOptionStore store = new();
    private readonly Place home;
    private readonly Place destination;
    private readonly Appointment appointment;
    private readonly DeparturePlan plan;

    public PlanPhaseTests()
    {
        home = new Place { Id = Guid.NewGuid(), Name = "Thuis", Lat = 52.0950, Lng = 5.2400 };
        destination = new Place { Id = Guid.NewGuid(), Name = "Utrecht Centraal", Lat = 52.0894, Lng = 5.1100 };
        appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            Title = "Dentist",
            Start = DateTimeOffset.Parse("2026-10-27T09:00:00Z"),
            End = DateTimeOffset.Parse("2026-10-27T10:00:00Z"),
            PlaceId = destination.Id,
            Place = destination,
            BufferMin = 5,
        };
        plan = new DeparturePlan
        {
            Id = Guid.NewGuid(),
            AppointmentId = appointment.Id,
            Version = 1,
            Mode = TravelMode.Bicycle,
            LeaveAt = DateTimeOffset.Parse("2026-10-27T08:37:00Z"),
            LeaveBy = DateTimeOffset.Parse("2026-10-27T08:42:00Z"), // buffer 5 min is a margin
            ArriveAt = DateTimeOffset.Parse("2026-10-27T08:55:00Z"),
            Reason = "test",
        };
        db.Context.AddRange(home, destination, appointment, plan,
            new UserSettings { Id = Guid.NewGuid(), LocationApiKey = "k", HomePlaceId = home.Id, EnabledModes = [TravelMode.Bicycle] });
        db.Context.SaveChanges();
    }

    private PlanPhaseService Service() => new(db.Context,
        new CurrentLocationProvider(buffer, clock), buffer, new PhaseOptions(), clock, NullLogger<PlanPhaseService>.Instance);

    private void FixAt(double lat, double lng) =>
        buffer.Points.Add(new LocationPoint(lat, lng, clock.GetUtcNow().AddMinutes(-1)));

    [Fact]
    public async Task Past_leaveAt_without_moving_becomes_leave_now_and_snapshots_the_reference()
    {
        // D6 (28 Oct): the buffer is a margin — between leaveAt and leaveBy the phase is
        // LeaveNow, not Late; leaving immediately still arrives on time.
        clock.SetUtcNow(plan.LeaveAt.AddMinutes(3)); // 08:40, leaveBy 08:42
        FixAt(home.Lat, home.Lng);

        var transitions = await Service().EvaluateAllAsync();

        var t = Assert.Single(transitions);
        Assert.Equal((PlanPhase.Upcoming, PlanPhase.LeaveNow), (t.From, t.To));
        Assert.Equal(PlanPhase.LeaveNow, plan.Phase);
        Assert.Equal(home.Lat, plan.LeaveFixLat);
    }

    [Fact]
    public async Task Past_leaveBy_without_moving_becomes_late()
    {
        clock.SetUtcNow(plan.LeaveBy.AddMinutes(2));
        FixAt(home.Lat, home.Lng);

        var transitions = await Service().EvaluateAllAsync();

        Assert.Equal(PlanPhase.Late, Assert.Single(transitions).To);
    }

    [Fact]
    public async Task Moving_from_the_reference_becomes_en_route_from_late()
    {
        clock.SetUtcNow(plan.LeaveBy.AddMinutes(1));
        FixAt(home.Lat, home.Lng);
        await Service().EvaluateAllAsync(); // → Late, reference snapshotted

        clock.SetUtcNow(plan.LeaveBy.AddMinutes(4));
        FixAt(home.Lat, home.Lng + 0.004); // ~270 m east

        var transitions = await Service().EvaluateAllAsync();

        Assert.Equal(PlanPhase.EnRoute, Assert.Single(transitions).To);
        Assert.Equal(PlanPhase.EnRoute, plan.Phase);
    }

    [Fact]
    public async Task Before_leaveAt_movement_never_reads_as_en_route()
    {
        // D6 amendment (28 Oct, live): before leaveAt the only reference would be home,
        // and ordinary morning drift — or an imprecise home — must not count as departing.
        FixAt(home.Lat, home.Lng + 0.004); // ~270 m from home, 25 min before leaveAt

        var transitions = await Service().EvaluateAllAsync();

        Assert.Empty(transitions);
        Assert.Equal(PlanPhase.Upcoming, plan.Phase);
    }

    [Fact]
    public async Task Leaving_early_becomes_en_route_once_leaveAt_passes()
    {
        FixAt(home.Lat, home.Lng + 0.004); // out the door 25 min early
        await Service().EvaluateAllAsync(); // still Upcoming (no reference yet)

        clock.SetUtcNow(plan.LeaveAt.AddSeconds(30));
        FixAt(home.Lat, home.Lng + 0.004); // reference snapshots here, at the early position
        await Service().EvaluateAllAsync();

        clock.SetUtcNow(plan.LeaveAt.AddMinutes(3));
        FixAt(home.Lat, home.Lng + 0.008); // another ~270 m along
        var transitions = await Service().EvaluateAllAsync();

        Assert.Equal(PlanPhase.EnRoute, Assert.Single(transitions).To);
    }

    [Fact]
    public async Task A_recommit_carries_the_phase_and_leave_fix_forward()
    {
        // D6 amendment (28 Oct, live): a mid-drive recommit reset Phase to Upcoming, which
        // let a fresh "leave now" nudge through while the user was already driving.
        plan.Phase = PlanPhase.EnRoute;
        plan.PhaseChangedAt = Now;
        (plan.LeaveFixLat, plan.LeaveFixLng) = (home.Lat, home.Lng);
        db.Context.SaveChanges();

        var plans = new PlanService(db.Context,
            new DeparturePlanner(routing, NullLogger<DeparturePlanner>.Instance),
            store, new CurrentLocationProvider(buffer, clock), clock, NullLogger<PlanService>.Instance);
        routing.BaseMinutes[TravelMode.Bicycle] = 40; // times shift well past jitter → new version
        var options = await plans.OptionsAsync(appointment.Id);
        var commit = await plans.CommitAsync(
            options.Options.First(o => o.Stops.Count == 0).OptionId, "mid-drive recovery", PlanCommitter.Agent);

        Assert.Equal(CommitStatus.Committed, commit.Status);
        Assert.Equal(2, commit.Plan!.Version);
        Assert.Equal(PlanPhase.EnRoute, commit.Plan.Phase);
        Assert.Equal(home.Lat, commit.Plan.LeaveFixLat);
        Assert.Equal(Now, commit.Plan.PhaseChangedAt);
    }

    [Fact]
    public async Task Two_far_apart_fixes_in_the_window_mean_moving()
    {
        // 28 Oct, live: sparse fixes left a drive undetected. Movement is EnRoute from
        // leaveAt − 15 min, regardless of direction or the 150 m leave-point rule.
        clock.SetUtcNow(plan.LeaveAt.AddMinutes(-10));
        buffer.Points.Add(new LocationPoint(home.Lat, home.Lng, clock.GetUtcNow().AddMinutes(-4)));
        buffer.Points.Add(new LocationPoint(home.Lat, home.Lng + 0.006, clock.GetUtcNow().AddMinutes(-1))); // ~410 m in 3 min

        var transitions = await Service().EvaluateAllAsync();

        var t = Assert.Single(transitions);
        Assert.Equal(PlanPhase.EnRoute, t.To);
        Assert.Contains("moving", t.Detail);
    }

    [Fact]
    public async Task Closing_in_on_the_destination_means_en_route()
    {
        // Single fresh fix, 15 min before leaveAt, much closer to the destination than home.
        clock.SetUtcNow(plan.LeaveAt.AddMinutes(-5));
        FixAt(52.0920, 5.1700); // well along the way to Utrecht Centraal

        var transitions = await Service().EvaluateAllAsync();

        var t = Assert.Single(transitions);
        Assert.Equal(PlanPhase.EnRoute, t.To);
        Assert.Contains("closer to", t.Detail);
    }

    [Fact]
    public async Task Within_the_arrived_radius_becomes_arrived()
    {
        plan.Phase = PlanPhase.EnRoute;
        db.Context.SaveChanges();
        FixAt(destination.Lat, destination.Lng);

        var transitions = await Service().EvaluateAllAsync();

        Assert.Equal(PlanPhase.Arrived, Assert.Single(transitions).To);
        Assert.NotNull(plan.PhaseChangedAt);
    }

    [Fact]
    public async Task No_fix_still_arrives_by_the_fallback_clock()
    {
        clock.SetUtcNow(appointment.Start.AddMinutes(16)); // past start + 15

        var transitions = await Service().EvaluateAllAsync();

        Assert.Contains(transitions, t => t.To == PlanPhase.Arrived);
    }

    [Fact]
    public async Task Transitions_are_recorded_as_phase_trace_rows()
    {
        clock.SetUtcNow(plan.LeaveBy.AddMinutes(3));
        FixAt(home.Lat, home.Lng);

        await Service().EvaluateAllAsync();

        var trace = Assert.Single(db.Context.AgentTraces.ToList());
        Assert.Equal("phase", trace.Trigger);
        Assert.Contains("Late", trace.FinalMessage);
    }

    [Fact]
    public async Task No_nudge_of_any_kind_once_en_route()
    {
        plan.Phase = PlanPhase.EnRoute;
        db.Context.SaveChanges();
        clock.SetUtcNow(plan.LeaveAt.AddMinutes(5));
        var nudges = new NudgeService(db.Context, store, new RecordingNudgeTransport(), clock, NullLogger<NudgeService>.Instance);

        var late = await nudges.SendAsync(NudgeKind.Late, "Leave now by bike, you'll arrive at 09:55.", appointment.Id, null);
        var leave = await nudges.SendAsync(NudgeKind.Leave, "Leave at 09:37 by bike.", appointment.Id, null);

        Assert.False(late.Sent);
        Assert.False(leave.Sent);
        Assert.Contains("on the way", late.RefusalReason);
    }

    [Fact]
    public async Task Late_refresh_moves_the_times_in_place_without_a_new_version()
    {
        var plans = PlanService();
        var options = await plans.OptionsAsync(appointment.Id);
        await plans.CommitAsync(options.Options[0].OptionId, "test", PlanCommitter.Agent); // v2 over the seeded v1
        var committed = (await plans.CurrentAsync(appointment.Id))!;
        var originalArrive = committed.ArriveAt;

        clock.SetUtcNow(committed.LeaveAt.AddMinutes(12)); // late: reality moved on
        FixAt(home.Lat, home.Lng);
        var refreshed = await plans.RefreshCommittedAsync(appointment.Id);

        Assert.NotNull(refreshed);
        Assert.Equal(committed.Version, refreshed!.Value.Plan.Version); // same version
        Assert.True(refreshed.Value.Plan.ArriveAt > originalArrive); // but honest times
        Assert.False(refreshed.Value.BetterAlternative); // only one mode enabled, no stops
    }

    [Fact]
    public async Task A_much_better_alternative_flags_the_agent_to_re_decide()
    {
        var cafe = new Place { Id = Guid.NewGuid(), Name = "Bar Bello", Lat = 52.0905, Lng = 5.2000 };
        db.Context.AddRange(cafe, new Habit
        {
            Id = Guid.NewGuid(),
            PlaceId = cafe.Id,
            Place = cafe,
            State = HabitState.Confirmed,
            AvgDurationMin = 8,
            VisitCount = 4,
            Context = new ContextKey(DayOfWeek.Tuesday, TimeBucket.Morning, TravelDirection.AwayFromHome, null),
        });
        db.Context.SaveChanges();
        var plans = PlanService();
        var options = await plans.OptionsAsync(appointment.Id);
        var withStop = options.Options.First(o => o.Stops.Count > 0); // 8 min stop + 4 detour
        await plans.CommitAsync(withStop.OptionId, "test", PlanCommitter.Agent);

        clock.SetUtcNow(withStop.LeaveAt.AddMinutes(20)); // deep in the late state
        FixAt(home.Lat, home.Lng);
        var refreshed = await plans.RefreshCommittedAsync(appointment.Id);

        Assert.True(refreshed!.Value.BetterAlternative); // dropping the stop wins by > 5 min
    }

    private PlanService PlanService()
    {
        FixAt(home.Lat, home.Lng);
        return new PlanService(db.Context,
            new DeparturePlanner(routing, NullLogger<DeparturePlanner>.Instance),
            store, new CurrentLocationProvider(buffer, clock), clock, NullLogger<PlanService>.Instance);
    }

    public void Dispose() => db.Dispose();
}
