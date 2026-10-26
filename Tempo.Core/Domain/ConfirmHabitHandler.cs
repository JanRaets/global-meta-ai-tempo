using Microsoft.EntityFrameworkCore;
using Tempo.Core.Data;
using Tempo.Core.Events;

namespace Tempo.Core.Domain;

/// <summary>
/// Sole producer of <see cref="ConfirmedHabit"/> (design D3). Telegram buttons, the app's
/// confirm button and the ConfirmHabit voice intent all route here; nothing else can
/// construct a ConfirmedHabit, enforced by the internal constructor and a reflection test.
/// </summary>
public sealed class ConfirmHabitHandler(TempoDbContext db, IDomainEvents events)
{
    public async Task<ConfirmedHabit> ConfirmAsync(Guid habitId, CancellationToken ct = default)
    {
        var habit = await db.Habits.Include(h => h.Place).SingleAsync(h => h.Id == habitId, ct);
        if (habit.State == HabitState.Rejected)
            throw new InvalidOperationException($"Habit {habitId} was rejected and cannot be confirmed");
        habit.State = HabitState.Confirmed;
        habit.DecidedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await events.PublishAsync(new HabitConfirmed(habit.Id), ct);
        return FromPersisted(habit);
    }

    public async Task RejectAsync(Guid habitId, CancellationToken ct = default)
    {
        var habit = await db.Habits.SingleAsync(h => h.Id == habitId, ct);
        habit.State = HabitState.Rejected;
        habit.DecidedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Rehydrates a ConfirmedHabit from an already-confirmed row (planner load path).</summary>
    public static ConfirmedHabit FromPersisted(Habit habit)
    {
        if (habit.State != HabitState.Confirmed)
            throw new InvalidOperationException($"Habit {habit.Id} is {habit.State}, not Confirmed");
        return new ConfirmedHabit(habit.Id, habit.PlaceId, habit.Place?.Name ?? "stop", habit.AvgDurationMin, habit.Context);
    }
}
