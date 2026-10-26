using FastEndpoints;
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
