using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Tempo.Agent;
using Tempo.Core.Data;
using Tempo.Core.Domain;
using Tempo.Core.Geo;

namespace Tempo.Api.Endpoints;

// Development-only harness: seed an appointment and run the agent inline, returning the
// trace. Used for task 7.8's live runs and again in section 13's demo prep.

public sealed class SeedAppointmentRequest
{
    public string Title { get; set; } = "Seeded appointment";
    public int InMinutes { get; set; } = 60;
    public double Lat { get; set; }
    public double Lng { get; set; }
    public string PlaceName { get; set; } = "Seeded place";
    public int BufferMin { get; set; } = 5;
}

public sealed class SeedAppointmentEndpoint(TempoDbContext db, PlaceResolver places, IWebHostEnvironment env, TimeProvider clock)
    : Endpoint<SeedAppointmentRequest>
{
    public override void Configure()
    {
        Post("/admin/seed-appointment");
        AllowAnonymous();
    }

    public override async Task HandleAsync(SeedAppointmentRequest req, CancellationToken ct)
    {
        if (!env.IsDevelopment())
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var place = await places.ResolvePointAsync(req.Lat, req.Lng, req.PlaceName, ct);
        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            Source = AppointmentSource.Captured,
            Title = req.Title,
            Start = clock.GetUtcNow().AddMinutes(req.InMinutes),
            End = clock.GetUtcNow().AddMinutes(req.InMinutes + 60),
            PlaceId = place.Id,
            BufferMin = req.BufferMin,
        };
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync(ct);
        await Send.OkAsync(new { appointmentId = appointment.Id, start = appointment.Start, place = place.Name }, ct);
    }
}

/// <summary>Seeds Visit history for habit inference — the section-13 safety net and the
/// 8.7 demo data. Scenarios: "coffee-week" (5 Tuesday-morning café stops before 09:30
/// meetings, 4 Thursday-evening supermarket stops toward home, 2 irregular visits that
/// must NOT become a habit) and "demo-tonight" (3 café stops matching today's and
/// tomorrow's weekday in the current time bucket, for live agent runs).</summary>
public sealed class SeedVisitsEndpoint(TempoDbContext db, Tempo.Core.Geo.PlaceResolver places, IWebHostEnvironment env, TimeProvider clock)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/admin/seed-visits");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!env.IsDevelopment())
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var scenario = Query<string?>("scenario", isRequired: false) ?? "coffee-week";
        var settings = await db.UserSettings.FirstAsync(ct);
        var tz = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone);
        var todayLocal = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), tz);

        var home = await places.ResolvePointAsync(52.0950, 5.2400, "Thuis", ct);
        var cafe = await places.ResolvePointAsync(52.0905, 5.2000, "Bar Bello", ct);
        await db.SaveChangesAsync(ct);
        settings.HomePlaceId ??= home.Id;
        db.UserSettings.Update(settings);

        var added = 0;
        void Visit(Guid placeId, DateTimeOffset arrive, int minutes)
        {
            db.Visits.Add(new Visit { Id = Guid.NewGuid(), PlaceId = placeId, ArrivedAt = arrive, DepartedAt = arrive.AddMinutes(minutes) });
            added++;
        }
        DateTimeOffset LocalPast(DayOfWeek day, int weeksBack, int hour, int minute)
        {
            var date = todayLocal.Date.AddDays(-(((todayLocal.DayOfWeek - day + 7) % 7) is 0 ? 7 : (todayLocal.DayOfWeek - day + 7) % 7));
            date = date.AddDays(-7 * (weeksBack - 1));
            var local = new DateTimeOffset(date.AddHours(hour).AddMinutes(minute), tz.GetUtcOffset(date.AddHours(hour)));
            return local.ToUniversalTime(); // Npgsql writes timestamptz as UTC only
        }

        if (scenario == "coffee-week")
        {
            var supermarket = await places.ResolvePointAsync(52.0920, 5.2100, "Albert Heijn", ct);
            var snackbar = await places.ResolvePointAsync(52.0930, 5.2150, "Snackbar Smul", ct);
            var office = await places.ResolvePointAsync(52.0894, 5.1100, "Utrecht Centraal", ct);
            await db.SaveChangesAsync(ct);
            for (var week = 1; week <= 5; week++)
            {
                Visit(home.Id, LocalPast(DayOfWeek.Tuesday, week, 7, 30), 70);   // home before → away-from-home
                Visit(cafe.Id, LocalPast(DayOfWeek.Tuesday, week, 8, 50), 9);
                db.Appointments.Add(new Appointment
                {
                    Id = Guid.NewGuid(),
                    Source = AppointmentSource.Captured,
                    Title = "Team meeting",
                    Start = LocalPast(DayOfWeek.Tuesday, week, 9, 30),
                    End = LocalPast(DayOfWeek.Tuesday, week, 10, 30),
                    PlaceId = office.Id,
                });
            }
            int[] superDurations = [14, 16, 15, 15];
            for (var week = 1; week <= 4; week++)
            {
                Visit(supermarket.Id, LocalPast(DayOfWeek.Thursday, week, 18, 10), superDurations[week - 1]);
                Visit(home.Id, LocalPast(DayOfWeek.Thursday, week, 18, 40), 140); // home after → toward-home
            }
            Visit(snackbar.Id, LocalPast(DayOfWeek.Wednesday, 1, 13, 0), 12);     // 2 visits: never a habit
            Visit(snackbar.Id, LocalPast(DayOfWeek.Wednesday, 3, 13, 10), 55);
        }
        else if (scenario == "demo-tonight")
        {
            foreach (var day in new[] { todayLocal.DayOfWeek, todayLocal.AddDays(1).DayOfWeek })
                for (var week = 1; week <= 3; week++)
                    Visit(cafe.Id, LocalPast(day, week, todayLocal.Hour, 0), 9);  // same bucket as now, direction Other
        }
        else
        {
            AddError($"unknown scenario '{scenario}'");
            await Send.ErrorsAsync(400, ct);
            return;
        }

        await db.SaveChangesAsync(ct);
        await Send.OkAsync(new { scenario, visitsAdded = added, homePlaceId = settings.HomePlaceId }, ct);
    }
}

public sealed class RunHabitInferenceEndpoint(Tempo.Jobs.HabitInferenceService inference, TempoDbContext db, IWebHostEnvironment env)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/admin/run-habit-inference");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!env.IsDevelopment())
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var created = await inference.RunAsync(ct);
        var proposed = await db.Habits.AsNoTracking().Include(h => h.Place)
            .Where(h => h.State == HabitState.Proposed).ToListAsync(ct);
        await Send.OkAsync(new
        {
            created,
            proposed = proposed.Select(h => new { id = h.Id, place = h.Place?.Name, sentence = h.ProposalText, rationale = h.Rationale }),
        }, ct);
    }
}

public sealed class AgentRunRequest
{
    public Guid AppointmentId { get; set; }
    public string Trigger { get; set; } = "dev:manual";
}

public sealed class AgentRunEndpoint(AgentRunner runner, IWebHostEnvironment env) : Endpoint<AgentRunRequest>
{
    public override void Configure()
    {
        Post("/admin/agent-run");
        AllowAnonymous();
    }

    public override async Task HandleAsync(AgentRunRequest req, CancellationToken ct)
    {
        if (!env.IsDevelopment())
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var trace = await runner.RunAsync(req.Trigger, req.AppointmentId, ct);
        if (trace is null)
        {
            await Send.OkAsync(new { skipped = true }, ct);
            return;
        }
        await Send.OkAsync(new
        {
            traceId = trace.Id,
            provider = trace.Provider?.ToString(),
            outcome = trace.Outcome.ToString(),
            durationMs = trace.DurationMs,
            steps = trace.Steps.OrderBy(s => s.Seq).Select(s => new { s.Seq, s.Tool, args = s.ArgsJson, result = s.ResultSummary }),
            finalMessage = trace.FinalMessage,
        }, ct);
    }
}
