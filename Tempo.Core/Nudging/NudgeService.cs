using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tempo.Core.Data;
using Tempo.Core.Domain;
using Tempo.Core.Planning;

namespace Tempo.Core.Nudging;

public sealed record NudgeSendResult(bool Sent, string? RefusalReason = null, Nudge? Nudge = null)
{
    public static NudgeSendResult Refused(string reason) => new(false, reason);
}

/// <summary>
/// notify.send — every guard lives here, not in the prompt (design D7/D8, nudging spec):
/// no committed plan, duplicate (planId, version, kind), quiet hours (late-within-60-min
/// exception), and text validation: the plan's leave time and mode must appear, at most two
/// sentences, and no time value that is not in the committed plan or the run's options.
/// </summary>
public sealed partial class NudgeService(
    TempoDbContext db,
    PlanOptionStore optionStore,
    INudgeTransport transport,
    TimeProvider clock,
    ILogger<NudgeService> logger)
{
    public async Task<NudgeSendResult> SendAsync(NudgeKind kind, string text, Guid appointmentId, Guid? traceId, CancellationToken ct = default)
    {
        var settings = await db.UserSettings.AsNoTracking().FirstAsync(ct);
        var tz = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone);
        var appointment = await db.Appointments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == appointmentId, ct);
        if (appointment is null)
            return NudgeSendResult.Refused("Refused: unknown appointment");

        var plan = await db.DeparturePlans
            .Where(p => p.AppointmentId == appointmentId)
            .OrderByDescending(p => p.Version)
            .FirstOrDefaultAsync(ct);
        if (plan is null)
            return NudgeSendResult.Refused("Refused: no committed plan — commit an option first");

        var duplicate = await db.Nudges.AnyAsync(n =>
            n.PlanId == plan.Id && n.PlanVersion == plan.Version && n.Kind == kind && n.Status != NudgeStatus.Cancelled, ct);
        if (duplicate)
            return NudgeSendResult.Refused("Refused: duplicate — this nudge was already sent for this plan version");

        var now = clock.GetUtcNow();
        // D8 (amended 27 Oct): the Leave window is enforced here, not only in the tick, so no
        // caller can nudge early. Late nudges are by definition past the window.
        if (kind == NudgeKind.Leave && now < plan.LeaveAt.AddMinutes(-settings.NudgeLeadMin))
        {
            var opensAt = TimeZoneInfo.ConvertTime(plan.LeaveAt.AddMinutes(-settings.NudgeLeadMin), tz);
            return NudgeSendResult.Refused($"Refused: nudge window not open yet (opens {opensAt:HH:mm})");
        }
        if (InQuietHours(TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, tz).DateTime), settings)
            && !(kind == NudgeKind.Late && appointment.Start - now <= TimeSpan.FromMinutes(60)))
            return NudgeSendResult.Refused("Refused: quiet hours");

        if (Validate(text, plan, kind, tz) is { } textProblem)
            return NudgeSendResult.Refused(textProblem);

        var nudge = new Nudge
        {
            Id = Guid.NewGuid(),
            AppointmentId = appointmentId,
            PlanId = plan.Id,
            PlanVersion = plan.Version,
            Kind = kind,
            Text = text,
            AgentTraceId = traceId,
        };
        db.Nudges.Add(nudge);
        await db.SaveChangesAsync(ct);
        try
        {
            nudge.Status = await transport.SendAsync(nudge, ct) ? NudgeStatus.Sent : NudgeStatus.Failed;
            nudge.SentAt = nudge.Status == NudgeStatus.Sent ? clock.GetUtcNow() : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Nudge transport failed: {Message}", ex.Message);
            nudge.Status = NudgeStatus.Failed;
        }
        await db.SaveChangesAsync(ct);
        return new NudgeSendResult(true, Nudge: nudge);
    }

    private string? Validate(string text, DeparturePlan plan, NudgeKind kind, TimeZoneInfo tz)
    {
        string Local(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, tz).ToString("HH:mm");

        // Leave nudges name the leave time. Late nudges name the recovery's arrival instead —
        // the leave time is already in the past and reads wrong (spec amended 27 Oct).
        if (kind == NudgeKind.Leave && !TimesIn(text).Contains(Local(plan.LeaveAt)))
            return "Refused: missing leave time — the text must contain the plan's leave time " + Local(plan.LeaveAt);
        if (kind == NudgeKind.Late && !TimesIn(text).Contains(Local(plan.ArriveAt)))
            return "Refused: missing arrival time — a late nudge must contain the recovery's arrival time " + Local(plan.ArriveAt);

        if (!ModeWords(plan.Mode).Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)))
            return $"Refused: missing mode — the text must name the mode ({plan.Mode})";

        if (SentenceCount(text) > 2)
            return "Refused: more than two sentences";

        var allowed = new HashSet<string> { Local(plan.LeaveAt), Local(plan.ArriveAt) };
        foreach (var (_, option) in optionStore.All())
        {
            allowed.Add(Local(option.LeaveAt));
            allowed.Add(Local(option.ArriveAt));
        }
        var unknown = TimesIn(text).FirstOrDefault(t => !allowed.Contains(t));
        return unknown is null ? null : $"Refused: unknown time {unknown} — every time must come from the plan or its options";
    }

    private static IReadOnlyList<string> TimesIn(string text) =>
        TimePattern().Matches(text)
            .Select(m => $"{int.Parse(m.Groups[1].Value):D2}:{m.Groups[2].Value}")
            .ToList();

    private static int SentenceCount(string text) =>
        text.Split(['.', '!', '?'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

    private static string[] ModeWords(TravelMode mode) => mode switch
    {
        TravelMode.Bicycle => ["bicycle", "bike", "cycle", "cycling", "fiets"],
        TravelMode.Walking => ["walk", "foot", "lopen", "wandel"],
        TravelMode.Car => ["car", "drive", "driving", "auto"],
        TravelMode.Train => ["train", "trein"],
        _ => [mode.ToString()],
    };

    private static bool InQuietHours(TimeOnly nowLocal, UserSettings settings) =>
        settings.QuietHoursStart <= settings.QuietHoursEnd
            ? nowLocal >= settings.QuietHoursStart && nowLocal < settings.QuietHoursEnd
            : nowLocal >= settings.QuietHoursStart || nowLocal < settings.QuietHoursEnd;

    [GeneratedRegex(@"\b(\d{1,2}):(\d{2})\b")]
    private static partial Regex TimePattern();
}
