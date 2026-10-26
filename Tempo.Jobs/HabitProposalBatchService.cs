using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tempo.Core.Data;
using Tempo.Core.Domain;
using Tempo.Core.Nudging;

namespace Tempo.Jobs;

/// <summary>
/// Sends proposed habits to the user, batched to at most one message per day
/// (habit-learning spec). Scheduled at 08:00 local, which also satisfies the quiet-hours
/// deferral (nudging spec). An unanswered proposal is re-sent once after 7 days.
/// </summary>
public sealed class HabitProposalBatchService(
    TempoDbContext db,
    INudgeTransport transport,
    TimeProvider clock,
    ILogger<HabitProposalBatchService> logger)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        var settings = await db.UserSettings.AsNoTracking().FirstAsync(ct);
        var tz = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone);
        var now = clock.GetUtcNow();
        var today = TimeZoneInfo.ConvertTime(now, tz).Date;

        var sentToday = (await db.Nudges.AsNoTracking()
                .Where(n => n.Kind == NudgeKind.HabitProposal)
                .ToListAsync(ct))
            .Any(n => TimeZoneInfo.ConvertTime(n.CreatedAt, tz).Date == today);
        if (sentToday)
            return; // at most one proposal message per day

        var due = (await db.Habits.Include(h => h.Place)
                .Where(h => h.State == HabitState.Proposed)
                .ToListAsync(ct))
            .Where(h => h.ProposalSentCount == 0
                || (h.ProposalSentCount == 1 && h.LastProposedAt is { } last && now - last >= TimeSpan.FromDays(7)))
            .ToList();
        if (due.Count == 0)
            return;

        var text = "I noticed something — tell me if I should plan for these:\n"
            + string.Join("\n", due.Select(h => $"• {h.ProposalText} ({h.Rationale})"));
        var nudge = new Nudge
        {
            Id = Guid.NewGuid(),
            AppointmentId = Guid.Empty,
            PlanId = Guid.Empty,
            PlanVersion = 0,
            Kind = NudgeKind.HabitProposal,
            Text = text,
        };
        db.Nudges.Add(nudge);
        nudge.Status = await transport.SendAsync(nudge, ct) ? NudgeStatus.Sent : NudgeStatus.Failed;
        nudge.SentAt = nudge.Status == NudgeStatus.Sent ? now : null;
        foreach (var habit in due)
        {
            habit.LastProposedAt = now;
            habit.ProposalSentCount++;
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Habit proposal batch: {Count} proposal(s) sent", due.Count);
    }
}

/// <summary>Hangfire entry point: daily at 08:00 local.</summary>
public sealed class HabitProposalBatchJob(HabitProposalBatchService batch)
{
    public Task RunAsync(CancellationToken ct) => batch.RunAsync(ct);
}
