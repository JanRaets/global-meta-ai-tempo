using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Tempo.Core.Data;
using Tempo.Core.Domain;
using Tempo.Core.Nudging;
using Tempo.Core.Planning;

namespace Tempo.Api.Endpoints;

// The PWA's JSON API (D12, task 12.1), all under /api so SPA routes like /now and
// /habits (push deep links) never collide with endpoints.

/// <summary>GET /api/now — everything the Now screen shows in one payload.</summary>
public sealed class NowEndpoint(TempoDbContext db, PlanService plans, TimeProvider clock) : EndpointWithoutRequest
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

        var appointment = (await db.Appointments.AsNoTracking().Include(a => a.Place).ToListAsync(ct))
            .Where(a => a.PlaceId != null && !a.IsVirtual && a.Start > now && a.Start <= now.AddHours(6))
            .OrderBy(a => a.Start)
            .FirstOrDefault();
        if (appointment is null)
        {
            await Send.OkAsync(new { state = "none", nudges = await RecentNudgesAsync(db, tz, 3, ct) }, ct);
            return;
        }

        var plan = await plans.CurrentAsync(appointment.Id, ct);
        var late = plan is not null && now > plan.LeaveAt;
        await Send.OkAsync(new
        {
            state = late ? "late" : "normal",
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
                reason = plan.Reason,
                travelMin = (int)Math.Max(0, (plan.ArriveAt - plan.LeaveAt).TotalMinutes - plan.Stops.Sum(s => s.DurationMin)),
            },
            // Server computes the minutes (D1 habit: clients display, they don't do time math).
            leaveInMin = plan is null ? (int?)null : (int)Math.Floor((plan.LeaveAt - now).TotalMinutes),
            lateByMin = late ? (int)Math.Ceiling((now - plan!.LeaveAt).TotalMinutes) : 0,
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

public sealed class PutSettingsEndpoint(TempoDbContext db) : Endpoint<PutSettingsRequest>
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
        if (req.DemoMode is { } demo)
            settings.DemoMode = demo;
        await db.SaveChangesAsync(ct);
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
