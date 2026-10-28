using FastEndpoints;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Tempo.Core.Configuration;
using Tempo.Core.Data;
using Tempo.Core.Domain;
using Tempo.Core.Nudging;
using Tempo.Core.Planning;

namespace Tempo.Api.Endpoints;

// The PWA's JSON API (D12, task 12.1), all under /api so SPA routes like /now and
// /habits (push deep links) never collide with endpoints.

/// <summary>GET /api/now — everything the Now screen shows in one payload. The state comes
/// from the deterministic plan phase (D6): normal, late, enroute, arrived (for ~30 s), none.</summary>
public sealed class NowEndpoint(TempoDbContext db, PlanService plans, PhaseOptions phaseOptions, TimeProvider clock) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/api/now");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var settings = await db.UserSettings.AsNoTracking().FirstAsync(ct);
        var tz = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone);
        var now = clock.GetUtcNow();
        string Fmt(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, tz).ToString("HH:mm");

        // Include just-started appointments: a late or en-route trip outlives the start time.
        var candidates = (await db.Appointments.AsNoTracking().Include(a => a.Place).ToListAsync(ct))
            .Where(a => a.PlaceId != null && !a.IsVirtual
                && a.Start > now.AddMinutes(-phaseOptions.ArrivedFallbackMin) && a.Start <= now.AddHours(6))
            .OrderBy(a => a.Start)
            .ToList();

        Appointment? appointment = null;
        DeparturePlan? plan = null;
        var arrivedRecently = false;
        foreach (var candidate in candidates)
        {
            var candidatePlan = await plans.CurrentAsync(candidate.Id, ct);
            if (candidatePlan?.Phase == PlanPhase.Arrived)
            {
                // "Made it" shows for ~30 s, then the next appointment takes over.
                if (candidatePlan.PhaseChangedAt is { } at && now - at <= TimeSpan.FromSeconds(30))
                {
                    (appointment, plan, arrivedRecently) = (candidate, candidatePlan, true);
                    break;
                }
                continue;
            }
            (appointment, plan) = (candidate, candidatePlan);
            break;
        }
        if (appointment is null)
        {
            await Send.OkAsync(new { state = "none", nudges = await RecentNudgesAsync(db, tz, 3, ct) }, ct);
            return;
        }

        var state = arrivedRecently ? "arrived"
            : plan?.Phase switch
            {
                PlanPhase.Late => "late",
                PlanPhase.EnRoute => "enroute",
                PlanPhase.AtDestination => "here", // D6 amendment: already at the destination
                PlanPhase.LeaveNow => "leavenow",  // D6 amendment: buffer is a margin
                _ => "normal",
            };
        await Send.OkAsync(new
        {
            state,
            appointment = new
            {
                id = appointment.Id,
                title = appointment.Title,
                start = Fmt(appointment.Start),
                place = appointment.Place!.Name,
            },
            plan = plan is null ? null : new
            {
                version = plan.Version,
                mode = plan.Mode.ToString().ToLowerInvariant(),
                leaveAt = Fmt(plan.LeaveAt),
                arriveAt = Fmt(plan.ArriveAt),
                stops = plan.Stops.Select(s => new { s.Name, s.DurationMin }),
                isFeasible = plan.IsFeasible,
                isEstimated = plan.IsEstimated,
                reason = plan.Reason,
                travelMin = (int)Math.Max(0, (plan.ArriveAt - plan.LeaveAt).TotalMinutes - plan.Stops.Sum(s => s.DurationMin)),
            },
            // Server computes the minutes (D1 habit: clients display, they don't do time math).
            leaveInMin = plan is null ? (int?)null : (int)Math.Floor((plan.LeaveAt - now).TotalMinutes),
            // Late counts from leaveBy — the minutes the arrival actually slips past the
            // start — not from the buffered target (D6, 28 Oct).
            lateByMin = state == "late" ? (int)Math.Ceiling((now - plan!.LeaveBy).TotalMinutes) : 0,
            // En route: the dominant number; arriveAt refreshes in place every tick (13.e).
            minutesRemaining = state == "enroute" ? Math.Max(0, (int)Math.Ceiling((plan!.ArriveAt - now).TotalMinutes)) : 0,
            nudges = await RecentNudgesAsync(db, tz, 3, ct),
        }, ct);
    }

    internal static async Task<object> RecentNudgesAsync(TempoDbContext db, TimeZoneInfo tz, int limit, CancellationToken ct)
        => (await db.Nudges.AsNoTracking().Where(n => n.Status == NudgeStatus.Sent).ToListAsync(ct))
            .OrderByDescending(n => n.CreatedAt) // client-side: SQLite can't order DateTimeOffset
            .Take(limit)
            .Select(n => new
            {
                id = n.Id,
                kind = n.Kind.ToString(),
                text = n.Text,
                time = TimeZoneInfo.ConvertTime(n.SentAt ?? n.CreatedAt, tz).ToString("HH:mm"),
                hasTrace = n.AgentTraceId != null,
            })
            .ToList();
}

/// <summary>GET /api/timeline/today — today's appointments, captured ones included (D12 Today).</summary>
public sealed class TimelineTodayEndpoint(TempoDbContext db, TimeProvider clock) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/api/timeline/today");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var settings = await db.UserSettings.AsNoTracking().FirstAsync(ct);
        var tz = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone);
        var today = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), tz).Date;
        string Fmt(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, tz).ToString("HH:mm");

        var appointments = (await db.Appointments.AsNoTracking().Include(a => a.Place).ToListAsync(ct))
            .Where(a => TimeZoneInfo.ConvertTime(a.Start, tz).Date == today)
            .OrderBy(a => a.Start)
            .ToList();
        var plansByAppointment = new Dictionary<Guid, DeparturePlan>();
        foreach (var plan in await db.DeparturePlans.AsNoTracking().ToListAsync(ct))
            if (plansByAppointment.TryGetValue(plan.AppointmentId, out var existing) is false || plan.Version > existing.Version)
                plansByAppointment[plan.AppointmentId] = plan;

        var now = clock.GetUtcNow();
        await Send.OkAsync(new
        {
            date = TimeZoneInfo.ConvertTime(now, tz).ToString("ddd d MMM"),
            items = appointments.Select(a => new
            {
                id = a.Id,
                title = a.Title,
                start = Fmt(a.Start),
                place = a.Place?.Name,
                isVirtual = a.IsVirtual,
                source = a.Source.ToString(),
                isPast = a.End < now,
                plan = a.PlaceId != null && plansByAppointment.TryGetValue(a.Id, out var p)
                    ? new
                    {
                        leaveAt = Fmt(p.LeaveAt),
                        mode = p.Mode.ToString().ToLowerInvariant(),
                        stops = p.Stops.Select(s => s.Name),
                        // Chained origins (D6 amendment): where this departure starts from.
                        noTravel = p.LeaveAt == p.ArriveAt && p.Stops.Count == 0,
                        originLabel = p.OriginKind switch
                        {
                            OriginKind.Current => "from here",
                            OriginKind.PreviousAppointment when p.OriginName is not null => $"from {p.OriginName}",
                            _ => (string?)null,
                        },
                    }
                    : null,
            }),
        }, ct);
    }
}

/// <summary>GET /api/nudges?limit=20 — nudge history, newest first.</summary>
public sealed class NudgesEndpoint(TempoDbContext db) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/api/nudges");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var limit = Math.Clamp(Query<int?>("limit", isRequired: false) ?? 20, 1, 100);
        var settings = await db.UserSettings.AsNoTracking().FirstAsync(ct);
        var tz = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone);
        var nudges = (await db.Nudges.AsNoTracking().ToListAsync(ct))
            .OrderByDescending(n => n.CreatedAt)
            .Take(limit)
            .Select(n => new
            {
                id = n.Id,
                kind = n.Kind.ToString(),
                text = n.Text,
                status = n.Status.ToString(),
                time = TimeZoneInfo.ConvertTime(n.SentAt ?? n.CreatedAt, tz).ToString("ddd HH:mm"),
                isReplacement = n.ReplacesNudgeId != null,
                hasTrace = n.AgentTraceId != null,
            });
        await Send.OkAsync(nudges.ToList(), ct);
    }
}

/// <summary>GET/PUT /api/settings (task 12.6). Quiet hours as "HH:mm" strings.</summary>
public sealed class GetSettingsEndpoint(TempoDbContext db) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/api/settings");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var settings = await db.UserSettings.AsNoTracking().FirstAsync(ct);
        var places = await db.Places.AsNoTracking().OrderBy(p => p.Name).Select(p => new { p.Id, p.Name }).ToListAsync(ct);
        var subscriptions = await db.PushSubscriptions.AsNoTracking().CountAsync(ct);
        await Send.OkAsync(new
        {
            homePlaceId = settings.HomePlaceId,
            homePlaceName = places.FirstOrDefault(p => p.Id == settings.HomePlaceId)?.Name,
            enabledModes = settings.EnabledModes.Select(m => m.ToString().ToLowerInvariant()),
            pinnedMode = settings.PinnedMode?.ToString().ToLowerInvariant(),
            nudgeLeadMin = settings.NudgeLeadMin,
            quietStart = settings.QuietHoursStart.ToString("HH:mm"),
            quietEnd = settings.QuietHoursEnd.ToString("HH:mm"),
            demoMode = settings.DemoMode,
            timeZone = settings.TimeZone,
            places,
            pushSubscriptions = subscriptions,
        }, ct);
    }
}

public sealed class PutSettingsRequest
{
    public Guid? HomePlaceId { get; set; }
    public List<string>? EnabledModes { get; set; }
    /// <summary>"" clears the pin; null leaves it untouched.</summary>
    public string? PinnedMode { get; set; }
    public int? NudgeLeadMin { get; set; }
    public string? QuietStart { get; set; }
    public string? QuietEnd { get; set; }
    public bool? DemoMode { get; set; }
}

public sealed class PutSettingsEndpoint(TempoDbContext db, Hangfire.IBackgroundJobClient jobs) : Endpoint<PutSettingsRequest>
{
    public override void Configure()
    {
        Put("/api/settings");
        AllowAnonymous();
    }

    public override async Task HandleAsync(PutSettingsRequest req, CancellationToken ct)
    {
        var settings = await db.UserSettings.FirstAsync(ct);
        if (req.HomePlaceId is { } home)
        {
            if (!await db.Places.AnyAsync(p => p.Id == home, ct))
            {
                await Send.StringAsync("unknown place", 400, cancellation: ct);
                return;
            }
            settings.HomePlaceId = home;
        }
        if (req.EnabledModes is { Count: > 0 } modes)
        {
            var parsed = new List<TravelMode>();
            foreach (var m in modes)
            {
                if (!Enum.TryParse<TravelMode>(m, ignoreCase: true, out var mode))
                {
                    await Send.StringAsync($"unknown mode '{m}'", 400, cancellation: ct);
                    return;
                }
                parsed.Add(mode);
            }
            settings.EnabledModes = parsed;
        }
        if (req.PinnedMode is { } pin)
            settings.PinnedMode = pin == "" ? null
                : Enum.TryParse<TravelMode>(pin, ignoreCase: true, out var mode) ? mode : settings.PinnedMode;
        if (req.NudgeLeadMin is { } lead)
            settings.NudgeLeadMin = Math.Clamp(lead, 0, 60);
        if (req.QuietStart is { } qs && TimeOnly.TryParse(qs, out var start))
            settings.QuietHoursStart = start;
        if (req.QuietEnd is { } qe && TimeOnly.TryParse(qe, out var end))
            settings.QuietHoursEnd = end;
        var startDemo = req.DemoMode == true && !settings.DemoMode; // flip detection
        var stopDemo = req.DemoMode == false && settings.DemoMode;
        if (req.DemoMode is { } demo)
            settings.DemoMode = demo;
        await db.SaveChangesAsync(ct);
        if (startDemo)
            jobs.Enqueue<Tempo.Jobs.DemoModeJob>(j => j.RunAsync(CancellationToken.None)); // 13.1/13.2
        if (stopDemo)
            jobs.Enqueue<Tempo.Jobs.DemoModeJob>(j => j.CleanupAsync(CancellationToken.None)); // demo artifacts go with the toggle
        await Send.OkAsync(new { saved = true }, ct);
    }
}

/// <summary>POST /api/push/test — sends a test notification to every subscription (task 12.6).
/// Goes straight to the transport: it is not a Nudge and bypasses no guards that matter.</summary>
public sealed class PushTestEndpoint(INudgeTransport transport) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/api/push/test");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var delivered = await transport.SendAsync(new Nudge
        {
            Id = Guid.NewGuid(),
            Kind = NudgeKind.Leave,
            Text = "Test notification — TEMPO can reach this phone.",
        }, ct);
        await Send.OkAsync(new { delivered }, ct);
    }
}
