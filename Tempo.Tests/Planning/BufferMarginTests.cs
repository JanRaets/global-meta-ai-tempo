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

/// <summary>D6 amendment (28 Oct 2026): the buffer is a margin, not a deadline — leaveAt is
/// the target, leaveBy the last on-time moment, LeaveNow sits between them, and Late (with
/// its recovery machinery) begins only past leaveBy.</summary>
public sealed class BufferMarginTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-27T08:00:00Z");

    private readonly TestDb db = new();
    private readonly FakeTimeProvider clock = new(Now);
    private readonly FakeLocationBuffer buffer = new();
    private readonly FakeRoutingClient routing = new();
    private readonly PlanOptionStore store = new();
    private readonly Appointment appointment;
    private readonly Place home;

    public BufferMarginTests()
    {
        home = new Place { Id = Guid.NewGuid(), Name = "Thuis", Lat = 52.0950, Lng = 5.2400 };
        var destination = new Place { Id = Guid.NewGuid(), Name = "Utrecht Centraal", Lat = 52.0894, Lng = 5.1100 };
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
        db.Context.AddRange(home, destination, appointment, new UserSettings
        {
            Id = Guid.NewGuid(),
            LocationApiKey = "k",
            HomePlaceId = home.Id,
            EnabledModes = [TravelMode.Bicycle],
        });
        db.Context.SaveChanges();
        buffer.Points.Add(new LocationPoint(home.Lat, home.Lng, Now.AddMinutes(-2)));
    }

    private PlanService Plans() => new(db.Context,
        new DeparturePlanner(routing, NullLogger<DeparturePlanner>.Instance),
        store, new CurrentLocationProvider(buffer, clock), clock, NullLogger<PlanService>.Instance);

    private PlanPhaseService Phases() => new(db.Context,
        new CurrentLocationProvider(buffer, clock), buffer, new PhaseOptions(), clock, NullLogger<PlanPhaseService>.Instance);

    private NudgeService Nudges(RecordingNudgeTransport transport) =>
        new(db.Context, store, transport, clock, NullLogger<NudgeService>.Instance);

    [Fact]
    public async Task Options_carry_both_leave_times_and_feasibility_ignores_the_buffer()
    {
        // 18 min bike: leaveAt 08:37 (buffered target), leaveBy 08:42 (last on-time moment).
        var early = await Plans().OptionsAsync(appointment.Id);
        var option = Assert.Single(early.Options);
        Assert.Equal(DateTimeOffset.Parse("2026-10-27T08:37:00Z"), option.LeaveAt);
        Assert.Equal(DateTimeOffset.Parse("2026-10-27T08:42:00Z"), option.LeaveBy);
        Assert.Equal(42, option.SlackMin);

        // At 08:40 — past leaveAt, before leaveBy — leaving now arrives 08:58 ≤ 09:00: feasible.
        clock.SetUtcNow(DateTimeOffset.Parse("2026-10-27T08:40:00Z"));
        var inside = Assert.Single((await Plans().OptionsAsync(appointment.Id)).Options);
        Assert.True(inside.IsFeasible);

        // At 08:45 — past leaveBy — arrival 09:03 slips past the start: infeasible.
        clock.SetUtcNow(DateTimeOffset.Parse("2026-10-27T08:45:00Z"));
        var past = Assert.Single((await Plans().OptionsAsync(appointment.Id)).Options);
        Assert.False(past.IsFeasible);
    }

    [Fact]
    public async Task Between_the_leave_times_the_phase_is_leave_now_then_late()
    {
        var plans = Plans();
        var options = await plans.OptionsAsync(appointment.Id);
        await plans.CommitAsync(options.Options[0].OptionId, "test", PlanCommitter.Agent);
        var plan = (await plans.CurrentAsync(appointment.Id))!;

        clock.SetUtcNow(plan.LeaveAt.AddMinutes(2)); // 08:39 — inside the margin
        buffer.Points.Add(new LocationPoint(home.Lat, home.Lng, clock.GetUtcNow().AddMinutes(-1)));
        Assert.Equal(PlanPhase.LeaveNow, Assert.Single(await Phases().EvaluateAllAsync()).To);

        clock.SetUtcNow(plan.LeaveBy.AddMinutes(1)); // 08:43 — past the last on-time moment
        buffer.Points.Add(new LocationPoint(home.Lat, home.Lng, clock.GetUtcNow().AddMinutes(-1)));
        Assert.Equal(PlanPhase.Late, Assert.Single(await Phases().EvaluateAllAsync()).To);
    }

    [Fact]
    public async Task A_leave_nudge_past_leaveBy_is_refused()
    {
        // Live 28 Oct: a Leave nudge went out with a leaveAt two days old — the window
        // must close as well as open; past leaveBy the late machinery owns the message.
        var plans = Plans();
        var options = await plans.OptionsAsync(appointment.Id);
        await plans.CommitAsync(options.Options[0].OptionId, "test", PlanCommitter.Agent);
        var plan = (await plans.CurrentAsync(appointment.Id))!;
        clock.SetUtcNow(plan.LeaveBy.AddMinutes(30));
        var nudges = Nudges(new RecordingNudgeTransport());

        var result = await nudges.SendAsync(NudgeKind.Leave, "Leave at 09:37 by bike.", appointment.Id, null);

        Assert.False(result.Sent);
        Assert.Contains("already past", result.RefusalReason);
    }

    [Fact]
    public async Task One_late_nudge_per_appointment_across_versions()
    {
        // 28 Oct, live: a mid-drive version bump re-sent the Late nudge ("arrive 08:19",
        // then "arrive 08:23"). Late is once per appointment, whatever the version does.
        var plans = Plans();
        var options = await plans.OptionsAsync(appointment.Id);
        await plans.CommitAsync(options.Options[0].OptionId, "v1", PlanCommitter.Agent);
        var plan = (await plans.CurrentAsync(appointment.Id))!;
        clock.SetUtcNow(plan.LeaveBy.AddMinutes(5));
        buffer.Points.Add(new LocationPoint(home.Lat, home.Lng, clock.GetUtcNow().AddMinutes(-1)));
        await Phases().EvaluateAllAsync(); // → Late
        var nudges = Nudges(new RecordingNudgeTransport());
        string Local(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t,
            TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam")).ToString("HH:mm");

        var first = await nudges.SendAsync(NudgeKind.Late,
            $"Leave now by bike — you'll arrive at {Local(plan.ArriveAt)}.", appointment.Id, null);
        Assert.True(first.Sent);

        // A recovery recommit bumps the version mid-drive.
        routing.BaseMinutes[TravelMode.Bicycle] = 10;
        var recovery = await plans.OptionsAsync(appointment.Id);
        await plans.CommitAsync(recovery.Options[0].OptionId, "recovery", PlanCommitter.Agent);
        var v2 = (await plans.CurrentAsync(appointment.Id))!;
        Assert.True(v2.Version > plan.Version);

        var second = await nudges.SendAsync(NudgeKind.Late,
            $"Leave now by bike — you'll arrive at {Local(v2.ArriveAt)}.", appointment.Id, null);
        Assert.False(second.Sent);
        Assert.Contains("already sent for this appointment", second.RefusalReason);
    }

    [Fact]
    public async Task A_demo_mode_nudge_is_tagged_demo()
    {
        db.Context.UserSettings.Single().DemoMode = true;
        db.Context.SaveChanges();
        var plans = Plans();
        var options = await plans.OptionsAsync(appointment.Id);
        await plans.CommitAsync(options.Options[0].OptionId, "test", PlanCommitter.Agent);
        var plan = (await plans.CurrentAsync(appointment.Id))!;
        clock.SetUtcNow(plan.LeaveAt.AddMinutes(-5)); // inside the window
        var nudges = Nudges(new RecordingNudgeTransport());
        string Local(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t,
            TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam")).ToString("HH:mm");

        var result = await nudges.SendAsync(NudgeKind.Leave,
            $"Leave at {Local(plan.LeaveAt)} by bike.", appointment.Id, null);

        Assert.True(result.Sent);
        Assert.Equal(NudgeSource.Demo, result.Nudge!.Source);
    }

    [Fact]
    public async Task One_leave_now_nudge_per_version_with_the_arrival_time()
    {
        var plans = Plans();
        var options = await plans.OptionsAsync(appointment.Id);
        await plans.CommitAsync(options.Options[0].OptionId, "test", PlanCommitter.Agent);
        var plan = (await plans.CurrentAsync(appointment.Id))!;
        var transport = new RecordingNudgeTransport();
        var nudges = Nudges(transport);
        string Local(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t,
            TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam")).ToString("HH:mm");

        // Before leaveAt the leave-now nudge is refused outright.
        var early = await nudges.SendAsync(NudgeKind.LeaveNow,
            $"Time to go — leave now by bike and you're there at {Local(plan.ArriveAt)}.", appointment.Id, null);
        Assert.False(early.Sent);

        clock.SetUtcNow(plan.LeaveAt.AddMinutes(1));
        buffer.Points.Add(new LocationPoint(home.Lat, home.Lng, clock.GetUtcNow().AddMinutes(-1)));
        await Phases().EvaluateAllAsync(); // → LeaveNow

        var bare = await nudges.SendAsync(NudgeKind.LeaveNow, "Time to go — leave now by bike.", appointment.Id, null);
        Assert.False(bare.Sent); // must name the arrival time

        var sent = await nudges.SendAsync(NudgeKind.LeaveNow,
            $"Time to go — leave now by bike and you're there at {Local(plan.ArriveAt)}.", appointment.Id, null);
        Assert.True(sent.Sent);

        var duplicate = await nudges.SendAsync(NudgeKind.LeaveNow,
            $"Time to go — leave now by bike and you're there at {Local(plan.ArriveAt)}.", appointment.Id, null);
        Assert.False(duplicate.Sent); // one per plan version
        Assert.Contains("duplicate", duplicate.RefusalReason);
    }

    public void Dispose() => db.Dispose();
}
