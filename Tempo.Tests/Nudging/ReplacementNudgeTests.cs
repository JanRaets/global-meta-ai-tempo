using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Tempo.Core.Domain;
using Tempo.Core.Nudging;
using Tempo.Core.Planning;
using Tempo.Tests.Fakes;

namespace Tempo.Tests.Nudging;

/// <summary>
/// Replacement-nudge guards in notify.send (amendment 29 Oct 2026): after the in-place
/// refresh moved leaveAt more than 5 min EARLIER past an already-sent Leave nudge, exactly
/// one replacement per appointment is allowed — it must name its cause in plain words, both
/// its times must be plan values, and a later leaveAt never re-nudges.
/// Plan: leave 09:37 local (08:37Z), shifted from 09:45 local, Bicycle, cause Traffic.
/// </summary>
public sealed class ReplacementNudgeTests : IDisposable
{
    private readonly TestDb db = new();
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-10-27T08:30:00Z")); // 09:30 local
    private readonly RecordingNudgeTransport transport = new();
    private readonly PlanOptionStore store = new();
    private readonly Appointment appointment;
    private readonly DeparturePlan plan;
    private readonly Nudge sentLeaveNudge;

    public ReplacementNudgeTests()
    {
        var place = new Place { Id = Guid.NewGuid(), Name = "Utrecht Centraal", Lat = 52.0894, Lng = 5.11 };
        appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            Title = "Dentist",
            Start = DateTimeOffset.Parse("2026-10-27T09:00:00Z"),
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
            LeaveBy = DateTimeOffset.Parse("2026-10-27T08:42:00Z"),
            ArriveAt = DateTimeOffset.Parse("2026-10-27T08:55:00Z"),
            ShiftReason = PlanShiftReason.Traffic,
            ShiftedFromLeaveAt = DateTimeOffset.Parse("2026-10-27T08:45:00Z"), // 8 min earlier now
            Reason = "test",
        };
        sentLeaveNudge = new Nudge
        {
            Id = Guid.NewGuid(),
            AppointmentId = appointment.Id,
            PlanId = plan.Id,
            PlanVersion = plan.Version,
            Kind = NudgeKind.Leave,
            Text = "Time to leave at 09:45 by bike for Dentist.",
            Status = NudgeStatus.Sent,
        };
        db.Context.AddRange(place, appointment, plan, sentLeaveNudge,
            new UserSettings { Id = Guid.NewGuid(), LocationApiKey = "k" });
        db.Context.SaveChanges();
    }

    private NudgeService Service() => new(db.Context, store, transport, clock, NullLogger<NudgeService>.Instance);

    private const string GoodReplacement = "Traffic's building — leave at 09:37 instead of 09:45, by bike for Dentist.";

    [Fact]
    public async Task One_replacement_is_allowed_and_links_the_nudge_it_replaces()
    {
        var result = await Service().SendAsync(NudgeKind.Leave, GoodReplacement, appointment.Id, null);

        Assert.True(result.Sent);
        Assert.Equal(sentLeaveNudge.Id, result.Nudge!.ReplacesNudgeId);
    }

    [Fact]
    public async Task A_second_replacement_is_refused()
    {
        Assert.True((await Service().SendAsync(NudgeKind.Leave, GoodReplacement, appointment.Id, null)).Sent);

        var second = await Service().SendAsync(NudgeKind.Leave, GoodReplacement, appointment.Id, null);

        Assert.False(second.Sent);
        Assert.Contains("duplicate", second.RefusalReason);
    }

    [Fact]
    public async Task A_replacement_without_its_cause_is_refused()
    {
        var result = await Service().SendAsync(NudgeKind.Leave,
            "Leave at 09:37 instead of 09:45, by bike for Dentist.", appointment.Id, null);

        Assert.False(result.Sent);
        Assert.Contains("cause", result.RefusalReason);
    }

    [Fact]
    public async Task A_later_leaveAt_never_renudges()
    {
        // Traffic cleared: the plan shifted LATER (was 09:30, now 09:37) — good news, no alarm.
        plan.ShiftedFromLeaveAt = DateTimeOffset.Parse("2026-10-27T08:30:00Z");
        await db.Context.SaveChangesAsync();

        var result = await Service().SendAsync(NudgeKind.Leave,
            "Traffic's building — leave at 09:37 instead of 09:30, by bike for Dentist.", appointment.Id, null);

        Assert.False(result.Sent);
        Assert.Contains("duplicate", result.RefusalReason);
    }

    public void Dispose() => db.Dispose();
}
