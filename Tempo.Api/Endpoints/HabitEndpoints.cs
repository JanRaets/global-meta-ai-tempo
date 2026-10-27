using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Tempo.Core.Data;
using Tempo.Core.Domain;

namespace Tempo.Api.Endpoints;

/// <summary>Habit management API (habit-learning spec): app + notification actions share these routes.</summary>
public sealed class ListHabitsEndpoint(TempoDbContext db) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/habits");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var stateFilter = Query<string?>("state", isRequired: false);
        var habits = await db.Habits.AsNoTracking().Include(h => h.Place).ToListAsync(ct);
        if (Enum.TryParse<HabitState>(stateFilter, ignoreCase: true, out var state))
            habits = habits.Where(h => h.State == state).ToList();
        await Send.OkAsync(habits.Select(h => new
        {
            id = h.Id,
            place = h.Place?.Name,
            state = h.State.ToString(),
            sentence = h.ProposalText,
            rationale = h.Rationale,
            avgDurationMin = h.AvgDurationMin,
            visitCount = h.VisitCount,
            weekday = h.Context.Weekday.ToString(),
            timeBucket = h.Context.TimeBucket.ToString(),
            direction = h.Context.Direction.ToString(),
            kind = h.Context.AdjacentAppointmentKind,
            decidedAt = h.DecidedAt,
        }).ToList(), ct);
    }
}

/// <summary>Runs the same ConfirmHabitHandler as the notification action and the voice intent;
/// the HabitConfirmed event re-runs the planner via the agent (habit-learning spec).</summary>
public sealed class ConfirmHabitEndpoint(ConfirmHabitHandler handler) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/habits/{id}/confirm");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        try
        {
            var confirmed = await handler.ConfirmAsync(Route<Guid>("id"), ct);
            await Send.OkAsync(new { confirmed = true, place = confirmed.PlaceName, avgMin = confirmed.AvgDurationMin }, ct);
        }
        catch (InvalidOperationException ex)
        {
            AddError(ex.Message);
            await Send.ErrorsAsync(409, ct);
        }
    }
}

public sealed class RejectHabitEndpoint(ConfirmHabitHandler handler) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/habits/{id}/reject");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await handler.RejectAsync(Route<Guid>("id"), ct);
        await Send.OkAsync(new { rejected = true }, ct);
    }
}
