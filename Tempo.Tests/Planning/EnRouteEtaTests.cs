using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Tempo.Core.Configuration;
using Tempo.Core.Domain;
using Tempo.Core.Location;
using Tempo.Core.Planning;
using Tempo.Tests.Fakes;

namespace Tempo.Tests.Planning;

/// <summary>Live ETA on departure (amended 29 Oct 2026, from real use: "On your way ·
/// arrive 08:15" survived minutes into a drive that arrived 08:26). The ingest-driven
/// refresh runs immediately on the post that flips a plan to EnRoute, then again on later
/// posts throttled to once per 60 s or 300 m moved; the slip number for the amber arrival
/// chip is server-computed and recovers to 0.</summary>
public sealed class EnRouteEtaTests : IDisposable
{
    private readonly TestDb db = new();
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-10-27T08:00:00Z"));
    private readonly FakeLocationBuffer buffer = new();
    private readonly FakeRoutingClient routing = new();
    private readonly EnRouteEtaThrottle throttle = new();
    private readonly PlanService plans;
    private readonly PlanPhaseService phases;
    private readonly EnRouteEtaService eta;
    private readonly Appointment appointment;

    public EnRouteEtaTests()
    {
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
        db.Context.AddRange(destination, appointment,
            new UserSettings { Id = Guid.NewGuid(), LocationApiKey = "k", EnabledModes = [TravelMode.Bicycle] });
        db.Context.SaveChanges();

        var location = new CurrentLocationProvider(buffer, clock);
        plans = new PlanService(db.Context,
            new DeparturePlanner(routing, NullLogger<DeparturePlanner>.Instance),
            new PlanOptionStore(), location, clock, NullLogger<PlanService>.Instance);
        phases = new PlanPhaseService(db.Context, location, buffer, new PhaseOptions(), clock,
            NullLogger<PlanPhaseService>.Instance);
        eta = new EnRouteEtaService(db.Context, plans, location, throttle, new PhaseOptions(), clock,
            NullLogger<EnRouteEtaService>.Instance);
    }

    /// <summary>Fix at 08:00, bike 18 min → committed plan leaves 08:37, arrives 08:55.
    /// Advances past leaveAt so the next moving fix flips the phase.</summary>
    private async Task<DeparturePlan> CommitAndReachLeaveAtAsync()
    {
        buffer.Points.Add(new LocationPoint(52.0906, 5.2332, clock.GetUtcNow().AddMinutes(-2)));
        var committed = await plans.FallbackCommitAsync(appointment.Id);
        Assert.NotNull(committed);

        clock.Advance(TimeSpan.FromMinutes(38)); // 08:38 — past leaveAt 08:37
        buffer.Points.Add(new LocationPoint(52.0906, 5.2332, clock.GetUtcNow()));
        await phases.EvaluateAllAsync(); // snapshots the leave reference, LeaveNow
        return committed!;
    }

    private async Task<IReadOnlyList<PhaseTransition>> PostMovingFixAsync(double lat, double lng)
    {
        buffer.Points.Add(new LocationPoint(lat, lng, clock.GetUtcNow()));
        return await phases.EvaluateAllAsync();
    }

    [Fact]
    public async Task The_post_that_flips_to_EnRoute_refreshes_the_eta_in_the_same_request()
    {
        var committed = await CommitAndReachLeaveAtAsync();
        var arriveBefore = committed.ArriveAt;

        clock.Advance(TimeSpan.FromMinutes(2));
        routing.BaseMinutes[TravelMode.Bicycle] = 30; // reality: the ride is slower than planned
        var transitions = await PostMovingFixAsync(52.0906, 5.2000); // ~2 km on the way
        Assert.Contains(transitions, t => t.To == PlanPhase.EnRoute);

        await eta.RefreshOnIngestAsync(transitions);

        var after = (await plans.CurrentAsync(appointment.Id))!;
        Assert.Equal(committed.Version, after.Version); // in place, no version bump
        Assert.NotEqual(arriveBefore, after.ArriveAt);  // no longer the pre-departure pin
    }

    [Fact]
    public async Task Posts_while_EnRoute_are_throttled_to_60s_or_300m()
    {
        await CommitAndReachLeaveAtAsync();
        clock.Advance(TimeSpan.FromMinutes(2));
        var transitions = await PostMovingFixAsync(52.0906, 5.2000);
        await eta.RefreshOnIngestAsync(transitions);
        var callsAfterFlip = routing.Origins.Count;

        // 15 s later, ~70 m further: inside both throttle limits → no refresh.
        clock.Advance(TimeSpan.FromSeconds(15));
        await eta.RefreshOnIngestAsync(await PostMovingFixAsync(52.0906, 5.1990));
        Assert.Equal(callsAfterFlip, routing.Origins.Count);

        // Another 15 s but ~700 m further: the 300 m leg lets the refresh through.
        clock.Advance(TimeSpan.FromSeconds(15));
        await eta.RefreshOnIngestAsync(await PostMovingFixAsync(52.0906, 5.1890));
        var callsAfterMove = routing.Origins.Count;
        Assert.True(callsAfterMove > callsAfterFlip);

        // 61 s later, barely moved: the 60 s leg lets the refresh through.
        clock.Advance(TimeSpan.FromSeconds(61));
        await eta.RefreshOnIngestAsync(await PostMovingFixAsync(52.0906, 5.1889));
        Assert.True(routing.Origins.Count > callsAfterMove);
    }

    [Fact]
    public void Arrival_past_the_start_turns_the_chip_amber()
    {
        // Projected 08:24 against an 08:20 start → "arriving 08:24 · 4 min late".
        Assert.Equal(4, Tempo.Api.Endpoints.NowEndpoint.ArriveSlipMin(
            DateTimeOffset.Parse("2026-10-29T07:24:00Z"), DateTimeOffset.Parse("2026-10-29T07:20:00Z")));
    }

    [Fact]
    public void A_recovered_eta_returns_the_chip_to_normal()
    {
        Assert.Equal(0, Tempo.Api.Endpoints.NowEndpoint.ArriveSlipMin(
            DateTimeOffset.Parse("2026-10-29T07:18:00Z"), DateTimeOffset.Parse("2026-10-29T07:20:00Z")));
    }

    public void Dispose() => db.Dispose();
}
