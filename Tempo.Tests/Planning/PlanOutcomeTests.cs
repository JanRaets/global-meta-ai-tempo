using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Tempo.Core.Configuration;
using Tempo.Core.Domain;
using Tempo.Core.Location;
using Tempo.Core.Planning;
using Tempo.Tests.Fakes;

namespace Tempo.Tests.Planning;

/// <summary>Outcome amendment (28 Oct 2026): past appointments show what happened, never a
/// plan — excluded from planning, outcome derived once from traces/fixes/visits.</summary>
public sealed class PlanOutcomeTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-28T10:00:00Z");

    private readonly TestDb db = new();
    private readonly FakeTimeProvider clock = new(Now);
    private readonly FakeLocationBuffer buffer = new();
    private readonly FakeRoutingClient routing = new();
    private readonly PlanOptionStore store = new();
    private readonly Place home;
    private readonly Place school;
    private readonly Appointment dropOff; // 08:20–09:20, already past at 10:00
    private readonly DeparturePlan plan;

    public PlanOutcomeTests()
    {
        home = new Place { Id = Guid.NewGuid(), Name = "Thuis", Lat = 52.0790, Lng = 5.2587 };
        school = new Place { Id = Guid.NewGuid(), Name = "School", Lat = 52.0850, Lng = 5.1439 };
        dropOff = new Appointment
        {
            Id = Guid.NewGuid(),
            Title = "Drop kids at school",
            Start = DateTimeOffset.Parse("2026-10-28T07:20:00Z"),
            End = DateTimeOffset.Parse("2026-10-28T08:20:00Z"),
            PlaceId = school.Id,
            Place = school,
            BufferMin = 5,
        };
        plan = new DeparturePlan
        {
            Id = Guid.NewGuid(),
            AppointmentId = dropOff.Id,
            Version = 2,
            Mode = TravelMode.Car,
            LeaveAt = DateTimeOffset.Parse("2026-10-28T06:56:00Z"),
            LeaveBy = DateTimeOffset.Parse("2026-10-28T07:01:00Z"),
            ArriveAt = DateTimeOffset.Parse("2026-10-28T07:15:00Z"),
            Reason = "test",
            LeaveFixLat = 52.0790,
            LeaveFixLng = 5.2587,
        };
        db.Context.AddRange(home, school, dropOff, plan, new UserSettings
        {
            Id = Guid.NewGuid(),
            LocationApiKey = "k",
            HomePlaceId = home.Id,
            EnabledModes = [TravelMode.Car],
        });
        db.Context.SaveChanges();
    }

    private PlanOutcomeService Outcomes() => new(db.Context, buffer, new PhaseOptions(), clock,
        NullLogger<PlanOutcomeService>.Instance);

    private void PhaseTrace(string message, string detail, string at) =>
        db.Context.AgentTraces.Add(new AgentTrace
        {
            Id = Guid.NewGuid(),
            Trigger = "phase",
            StartedAt = DateTimeOffset.Parse(at),
            Outcome = TraceOutcome.NoAction,
            FinalMessage = message,
            Steps = [new TraceStep(1, "phase", "{}", detail)],
        });

    [Fact]
    public async Task A_past_appointment_is_excluded_from_planning()
    {
        buffer.Points.Add(new LocationPoint(home.Lat, home.Lng, Now.AddMinutes(-2)));
        var plans = new PlanService(db.Context,
            new DeparturePlanner(routing, NullLogger<DeparturePlanner>.Instance),
            store, new CurrentLocationProvider(buffer, clock), clock, NullLogger<PlanService>.Instance);

        var result = await plans.OptionsAsync(dropOff.Id);

        Assert.Empty(result.Options);
        Assert.Contains("frozen", result.Note);
        Assert.Empty(routing.Origins); // no routing calls for the past
    }

    [Fact]
    public async Task On_time_outcome_from_the_phase_traces()
    {
        PhaseTrace("Drop kids at school: Upcoming → EnRoute", "Moved 300 m from the leave point — on the way.",
            "2026-10-28T06:58:00Z");
        PhaseTrace("Drop kids at school: EnRoute → Arrived", "Within 100 m of School — made it.",
            "2026-10-28T07:15:00Z");
        db.Context.SaveChanges();
        var visitsBefore = db.Context.Visits.Count();

        var derived = (await Outcomes().EnsureAsync(dropOff))!;

        Assert.Equal(PlanOutcomeKind.OnTime, derived.OutcomeKind);
        Assert.Equal(DateTimeOffset.Parse("2026-10-28T06:58:00Z"), derived.LeftAt);
        Assert.Equal(DateTimeOffset.Parse("2026-10-28T07:15:00Z"), derived.ActualArriveAt);
        Assert.Equal(visitsBefore, db.Context.Visits.Count()); // derivation never creates Visits
    }

    [Fact]
    public async Task Late_outcome_from_the_overlapping_visit()
    {
        // No Arrived-by-proximity trace; the Visit at the destination is the evidence.
        db.Context.Visits.Add(new Visit
        {
            Id = Guid.NewGuid(),
            PlaceId = school.Id,
            ArrivedAt = DateTimeOffset.Parse("2026-10-28T07:26:00Z"), // 6 min after start
            DepartedAt = DateTimeOffset.Parse("2026-10-28T07:40:00Z"),
        });
        db.Context.SaveChanges();

        var derived = (await Outcomes().EnsureAsync(dropOff))!;

        Assert.Equal(PlanOutcomeKind.Late, derived.OutcomeKind);
        Assert.Equal(6, derived.OutcomeLateMin);
    }

    [Fact]
    public async Task The_fallback_arrival_assumption_is_not_evidence()
    {
        PhaseTrace("Drop kids at school: Upcoming → Arrived", "The appointment started 15 min ago — assuming arrival.",
            "2026-10-28T07:35:00Z");
        db.Context.SaveChanges();

        var derived = (await Outcomes().EnsureAsync(dropOff))!;

        Assert.Equal(PlanOutcomeKind.NoData, derived.OutcomeKind);
        Assert.Null(derived.ActualArriveAt);
    }

    [Fact]
    public async Task No_data_and_the_outcome_is_derived_only_once()
    {
        var first = (await Outcomes().EnsureAsync(dropOff))!;
        Assert.Equal(PlanOutcomeKind.NoData, first.OutcomeKind);

        // Evidence arriving later must not rewrite history.
        PhaseTrace("Drop kids at school: EnRoute → Arrived", "Within 100 m of School — made it.",
            "2026-10-28T07:15:00Z");
        db.Context.SaveChanges();
        var second = (await Outcomes().EnsureAsync(dropOff))!;

        Assert.Equal(PlanOutcomeKind.NoData, second.OutcomeKind);
    }

    public void Dispose() => db.Dispose();
}
