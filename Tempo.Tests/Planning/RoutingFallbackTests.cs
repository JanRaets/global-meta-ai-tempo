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

/// <summary>Design D18: straight-line fallback when routing is down for every mode;
/// real options only on partial outages; in-place recovery once routing answers.</summary>
public sealed class RoutingFallbackTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-27T08:00:00Z");

    private readonly TestDb db = new();
    private readonly FakeTimeProvider clock = new(Now);
    private readonly FakeLocationBuffer buffer = new();
    private readonly FakeRoutingClient routing = new();
    private readonly PlanOptionStore store = new();
    private readonly Appointment appointment;

    public RoutingFallbackTests()
    {
        var home = new Place { Id = Guid.NewGuid(), Name = "Thuis", Lat = 52.0950, Lng = 5.2400 };
        var destination = new Place { Id = Guid.NewGuid(), Name = "Utrecht Centraal", Lat = 52.0894, Lng = 5.1100 };
        appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            Title = "Dentist",
            Start = DateTimeOffset.Parse("2026-10-27T10:00:00Z"),
            End = DateTimeOffset.Parse("2026-10-27T11:00:00Z"),
            PlaceId = destination.Id,
            Place = destination,
            BufferMin = 5,
        };
        db.Context.AddRange(home, destination, appointment, new UserSettings
        {
            Id = Guid.NewGuid(),
            LocationApiKey = "k",
            HomePlaceId = home.Id,
            EnabledModes = [TravelMode.Bicycle, TravelMode.Car],
        });
        db.Context.SaveChanges();
        buffer.Points.Add(new LocationPoint(home.Lat, home.Lng, Now.AddMinutes(-2)));
    }

    private PlanService Service(bool fallbackEnabled = true) => new(db.Context,
        new DeparturePlanner(routing, NullLogger<DeparturePlanner>.Instance,
            new RoutingOptions { FallbackEnabled = fallbackEnabled }),
        store, new CurrentLocationProvider(buffer, clock), clock, NullLogger<PlanService>.Instance);

    [Fact]
    public async Task Every_mode_down_produces_estimated_options_with_the_note()
    {
        routing.Unavailable.Add(TravelMode.Bicycle);
        routing.Unavailable.Add(TravelMode.Car);

        var result = await Service().OptionsAsync(appointment.Id);

        Assert.NotEmpty(result.Options);
        Assert.All(result.Options, o => Assert.True(o.IsEstimated));
        Assert.Equal("routing unavailable, used estimate", result.Note);
        // ~8.8 km × 1.3 at 15 km/h ≈ 50 min by bike — sane straight-line arithmetic
        var bike = result.Options.First(o => o.Mode == TravelMode.Bicycle);
        var travel = (bike.ArriveAt - bike.LeaveAt).TotalMinutes;
        Assert.InRange(travel, 30, 70);
    }

    [Fact]
    public async Task Partial_outage_keeps_real_options_only()
    {
        routing.Unavailable.Add(TravelMode.Bicycle);

        var result = await Service().OptionsAsync(appointment.Id);

        Assert.All(result.Options, o => Assert.False(o.IsEstimated));
        Assert.All(result.Options, o => Assert.Equal(TravelMode.Car, o.Mode));
        Assert.Contains("routing unavailable for: Bicycle", result.Note);
    }

    [Fact]
    public async Task The_config_switch_disables_the_fallback()
    {
        routing.Unavailable.Add(TravelMode.Bicycle);
        routing.Unavailable.Add(TravelMode.Car);

        var result = await Service(fallbackEnabled: false).OptionsAsync(appointment.Id);

        Assert.Empty(result.Options);
    }

    [Fact]
    public async Task Recovery_replaces_the_estimate_in_place_or_wakes_the_agent_on_a_big_shift()
    {
        routing.Unavailable.Add(TravelMode.Bicycle);
        routing.Unavailable.Add(TravelMode.Car);
        var plans = Service();
        var options = await plans.OptionsAsync(appointment.Id);
        var estimated = options.Options.First(o => o.Mode == TravelMode.Car && o.Stops.Count == 0);
        await plans.CommitAsync(estimated.OptionId, "estimated commit", PlanCommitter.Agent);
        Assert.True((await plans.CurrentAsync(appointment.Id))!.IsEstimated);

        // ORS answers again. Real car time (12 min) differs from the ~19 min estimate ≤ 5 min? No:
        // straight-line car ≈ 20 min vs real 12 min → leaveAt shifts > 5 min → agent re-decides.
        routing.Unavailable.Clear();
        var refreshed = await plans.RefreshCommittedAsync(appointment.Id);
        Assert.True(refreshed!.Value.RecoveredWithShift);
        Assert.True((await plans.CurrentAsync(appointment.Id))!.IsEstimated); // untouched, agent will commit

        // A small shift instead: make the real estimate match the straight-line one.
        routing.BaseMinutes[TravelMode.Car] = 21;
        var again = await plans.RefreshCommittedAsync(appointment.Id);
        Assert.False(again!.Value.RecoveredWithShift);
        var plan = (await plans.CurrentAsync(appointment.Id))!;
        Assert.False(plan.IsEstimated); // replaced in place
        Assert.Equal(1, plan.Version); // same version as the estimated commit — no version bump
    }

    [Fact]
    public async Task An_estimated_plan_nudge_must_say_roughly()
    {
        routing.Unavailable.Add(TravelMode.Bicycle);
        routing.Unavailable.Add(TravelMode.Car);
        var plans = Service();
        var options = await plans.OptionsAsync(appointment.Id);
        var estimated = options.Options.First(o => o.Stops.Count == 0);
        await plans.CommitAsync(estimated.OptionId, "estimated commit", PlanCommitter.Agent);
        var plan = (await plans.CurrentAsync(appointment.Id))!;
        clock.SetUtcNow(plan.LeaveAt.AddMinutes(-5)); // inside the nudge window
        string Local(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t,
            TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam")).ToString("HH:mm");
        var mode = plan.Mode.ToString().ToLowerInvariant();
        var nudges = new NudgeService(db.Context, store, new RecordingNudgeTransport(), clock, NullLogger<NudgeService>.Instance);

        var bare = await nudges.SendAsync(NudgeKind.Leave, $"Leave at {Local(plan.LeaveAt)} by {mode}.", appointment.Id, null);
        Assert.False(bare.Sent);
        Assert.Contains("roughly", bare.RefusalReason);

        var rough = await nudges.SendAsync(NudgeKind.Leave, $"Leave at roughly {Local(plan.LeaveAt)} by {mode}.", appointment.Id, null);
        Assert.True(rough.Sent);
    }

    public void Dispose() => db.Dispose();
}
