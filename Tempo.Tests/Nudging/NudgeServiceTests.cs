using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Tempo.Core.Domain;
using Tempo.Core.Nudging;
using Tempo.Core.Planning;
using Tempo.Tests.Fakes;

namespace Tempo.Tests.Nudging;

public sealed class RecordingNudgeTransport : INudgeTransport
{
    public List<Nudge> Delivered { get; } = [];

    public Task<bool> SendAsync(Nudge nudge, CancellationToken ct = default)
    {
        Delivered.Add(nudge);
        return Task.FromResult(true);
    }
}

/// <summary>Plan: leave 09:37 local (08:37Z), arrive 09:55 local, Bicycle. Quiet hours 22–07.</summary>
public sealed class NudgeServiceTests : IDisposable
{
    private readonly TestDb db = new();
    // 09:30 local — inside the nudge window (opens 09:27 local for the 09:37 plan).
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-10-27T08:30:00Z"));
    private readonly RecordingNudgeTransport transport = new();
    private readonly PlanOptionStore store = new();
    private readonly Appointment appointment;
    private readonly DeparturePlan plan;

    public NudgeServiceTests()
    {
        var place = new Place { Id = Guid.NewGuid(), Name = "Utrecht Centraal", Lat = 52.0894, Lng = 5.11 };
        appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            Title = "Dentist",
            Start = DateTimeOffset.Parse("2026-10-27T09:00:00Z"), // 10:00 local
            End = DateTimeOffset.Parse("2026-10-27T10:00:00Z"),
            PlaceId = place.Id,
        };
        plan = new DeparturePlan
        {
            Id = Guid.NewGuid(),
            AppointmentId = appointment.Id,
            Version = 1,
            Mode = TravelMode.Bicycle,
            LeaveAt = DateTimeOffset.Parse("2026-10-27T08:37:00Z"),
            ArriveAt = DateTimeOffset.Parse("2026-10-27T08:55:00Z"),
            Reason = "test",
        };
        db.Context.AddRange(place, appointment, plan,
            new UserSettings { Id = Guid.NewGuid(), LocationApiKey = "k" });
        db.Context.SaveChanges();
    }

    private NudgeService Service() => new(db.Context, store, transport, clock, NullLogger<NudgeService>.Instance);

    [Fact]
    public async Task Valid_leave_nudge_is_sent_and_recorded()
    {
        var result = await Service().SendAsync(NudgeKind.Leave, "Leave at 09:37 by bike to make the dentist.", appointment.Id, null);

        Assert.True(result.Sent);
        Assert.Equal(NudgeStatus.Sent, result.Nudge!.Status);
        Assert.Single(transport.Delivered);
    }

    [Fact]
    public async Task Missing_leave_time_is_refused()
    {
        var result = await Service().SendAsync(NudgeKind.Leave, "Time to go by bike!", appointment.Id, null);

        Assert.False(result.Sent);
        Assert.Contains("missing leave time", result.RefusalReason);
        Assert.Empty(db.Context.Nudges);
    }

    [Fact]
    public async Task Unknown_time_is_refused_with_the_time_named()
    {
        var result = await Service().SendAsync(NudgeKind.Leave, "Leave at 09:37 by bike, you'll be there 09:15.", appointment.Id, null);

        Assert.False(result.Sent);
        Assert.Contains("unknown time 09:15", result.RefusalReason);
    }

    [Fact]
    public async Task Option_times_from_this_runs_options_are_allowed()
    {
        store.ReplaceFor(appointment.Id,
            [new PlanOption("abc12345", TravelMode.Bicycle, [], plan.LeaveAt, DateTimeOffset.Parse("2026-10-27T08:58:00Z"), true, 10)]);

        var result = await Service().SendAsync(NudgeKind.Leave, "Leave at 09:37 by bike, arriving 09:58.", appointment.Id, null);

        Assert.True(result.Sent);
    }

    [Fact]
    public async Task Missing_mode_is_refused()
    {
        var result = await Service().SendAsync(NudgeKind.Leave, "Leave at 09:37 for the dentist.", appointment.Id, null);

        Assert.False(result.Sent);
        Assert.Contains("missing mode", result.RefusalReason);
    }

    [Fact]
    public async Task Three_sentences_are_refused()
    {
        var result = await Service().SendAsync(NudgeKind.Leave,
            "Leave at 09:37. Take the bike. It will be fun.", appointment.Id, null);

        Assert.False(result.Sent);
        Assert.Contains("two sentences", result.RefusalReason);
    }

    [Fact]
    public async Task Duplicate_for_same_plan_version_and_kind_is_refused()
    {
        await Service().SendAsync(NudgeKind.Leave, "Leave at 09:37 by bike.", appointment.Id, null);

        var second = await Service().SendAsync(NudgeKind.Leave, "Leave at 09:37 by bike.", appointment.Id, null);

        Assert.False(second.Sent);
        Assert.Contains("duplicate", second.RefusalReason);
        Assert.Single(db.Context.Nudges);
    }

    [Fact]
    public async Task No_committed_plan_is_refused()
    {
        db.Context.DeparturePlans.Remove(plan);
        db.Context.SaveChanges();

        var result = await Service().SendAsync(NudgeKind.Leave, "Leave at 09:37 by bike.", appointment.Id, null);

        Assert.False(result.Sent);
        Assert.Contains("no committed plan", result.RefusalReason);
    }

    [Fact]
    public async Task Quiet_hours_suppress_a_leave_nudge()
    {
        clock.SetUtcNow(DateTimeOffset.Parse("2026-10-27T21:30:00Z")); // 22:30 local

        var result = await Service().SendAsync(NudgeKind.Leave, "Leave at 09:37 by bike.", appointment.Id, null);

        Assert.False(result.Sent);
        Assert.Contains("quiet hours", result.RefusalReason);
    }

    [Fact]
    public async Task Late_nudge_for_an_appointment_within_60_minutes_pierces_quiet_hours()
    {
        clock.SetUtcNow(DateTimeOffset.Parse("2026-10-27T21:30:00Z")); // 22:30 local
        appointment.Start = clock.GetUtcNow().AddMinutes(45);
        db.Context.SaveChanges();

        var result = await Service().SendAsync(NudgeKind.Late, "You're running late — leave now by bike to arrive at 09:55.", appointment.Id, null);

        Assert.True(result.Sent);
    }

    [Fact]
    public async Task Leave_nudge_before_the_window_opens_is_refused()
    {
        var earlyClock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-27T08:00:00Z")); // window opens 08:27Z
        var service = new NudgeService(db.Context, store, transport, earlyClock, NullLogger<NudgeService>.Instance);

        var result = await service.SendAsync(NudgeKind.Leave, "Leave at 09:37 by bike.", appointment.Id, null);

        Assert.False(result.Sent);
        Assert.Contains("window not open", result.RefusalReason);
        Assert.Contains("09:27", result.RefusalReason);
    }

    [Fact]
    public async Task Late_nudge_without_the_arrival_time_is_refused()
    {
        var result = await Service().SendAsync(NudgeKind.Late, "Leave now by bike, you are late.", appointment.Id, null);

        Assert.False(result.Sent);
        Assert.Contains("missing arrival time", result.RefusalReason);
    }

    [Fact]
    public async Task Late_nudge_with_arrival_and_no_leave_time_is_valid()
    {
        var result = await Service().SendAsync(NudgeKind.Late, "Leave now by bike and you'll arrive at 09:55.", appointment.Id, null);

        Assert.True(result.Sent);
    }

    /// <summary>Adds a v2 plan whose leaveAt shifted by <paramref name="shiftMin"/> minutes.</summary>
    private DeparturePlan AddPlanV2(int shiftMin)
    {
        var v2 = new DeparturePlan
        {
            Id = Guid.NewGuid(),
            AppointmentId = appointment.Id,
            Version = 2,
            Mode = TravelMode.Bicycle,
            LeaveAt = plan.LeaveAt.AddMinutes(shiftMin),
            ArriveAt = plan.ArriveAt.AddMinutes(shiftMin),
            Reason = "test v2",
        };
        db.Context.DeparturePlans.Add(v2);
        db.Context.SaveChanges();
        return v2;
    }

    [Fact]
    public async Task A_nudged_plan_that_shifts_more_than_five_minutes_sends_a_replacement()
    {
        var first = await Service().SendAsync(NudgeKind.Leave, "Leave at 09:37 by bike.", appointment.Id, null);
        AddPlanV2(shiftMin: -10); // leave 09:27 local; window already open

        var second = await Service().SendAsync(NudgeKind.Leave, "Leave at 09:27 by bike.", appointment.Id, null);

        Assert.True(second.Sent);
        Assert.Equal(first.Nudge!.Id, second.Nudge!.ReplacesNudgeId);
    }

    [Fact]
    public async Task A_shift_of_five_minutes_or_less_is_not_marked_as_a_replacement()
    {
        await Service().SendAsync(NudgeKind.Leave, "Leave at 09:37 by bike.", appointment.Id, null);
        AddPlanV2(shiftMin: -4); // leave 09:33 local

        var second = await Service().SendAsync(NudgeKind.Leave, "Leave at 09:33 by bike.", appointment.Id, null);

        Assert.True(second.Sent);
        Assert.Null(second.Nudge!.ReplacesNudgeId);
    }

    public void Dispose() => db.Dispose();
}
