using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tempo.Core.Data;
using Tempo.Core.Domain;
using Tempo.Core.Models;

namespace Tempo.Jobs;

/// <summary>
/// Nightly (and on-demand) habit inference (design D5, habit-learning spec): completed
/// Visits grouped by (placeId, contextKey); a group with at least 3 visits and a duration
/// standard deviation of at most 8 minutes becomes a Proposed habit. Thresholds and all
/// numbers are deterministic; Muse Spark only words the proposal.
/// </summary>
public sealed class HabitInferenceService(
    TempoDbContext db,
    HabitWording wording,
    TimeProvider clock,
    ILogger<HabitInferenceService> logger)
{
    private const int MinVisits = 3;
    private const double MaxStdDevMin = 8;
    private static readonly TimeSpan RejectedSuppression = TimeSpan.FromDays(30);

    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var settings = await db.UserSettings.AsNoTracking().FirstAsync(ct);
        var tz = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone);
        var visits = (await db.Visits.AsNoTracking().Include(v => v.Place).ToListAsync(ct))
            .Where(v => v.DepartedAt is not null)
            .OrderBy(v => v.ArrivedAt)
            .ToList();
        var appointments = await db.Appointments.AsNoTracking().ToListAsync(ct);
        var habits = await db.Habits.Include(h => h.Place).ToListAsync(ct);
        var now = clock.GetUtcNow();
        var created = 0;

        var groups = visits
            .GroupBy(v => (v.PlaceId, Context: DeriveContext(v, visits, appointments, settings.HomePlaceId, tz)))
            .Where(g => g.Count() >= MinVisits);

        foreach (var group in groups)
        {
            if (group.Key.PlaceId == settings.HomePlaceId)
                continue; // home is the anchor of directions, never a stop habit
            var durations = group.Select(v => (v.DepartedAt!.Value - v.ArrivedAt).TotalMinutes).ToList();
            if (StdDev(durations) > MaxStdDevMin)
                continue;
            var avg = Math.Round(durations.Average());
            var count = group.Count();
            var place = group.First().Place!;

            var existing = habits.FirstOrDefault(h => h.PlaceId == group.Key.PlaceId && h.Context == group.Key.Context);
            if (existing is not null)
            {
                switch (existing.State)
                {
                    case HabitState.Confirmed:
                        continue;
                    case HabitState.Proposed:
                        existing.AvgDurationMin = avg;
                        existing.VisitCount = count;
                        continue;
                    case HabitState.Rejected when now - existing.DecidedAt < RejectedSuppression:
                        continue;
                    case HabitState.Rejected:
                        existing.State = HabitState.Proposed;
                        existing.DecidedAt = null;
                        existing.AvgDurationMin = avg;
                        existing.VisitCount = count;
                        existing.ProposalSentCount = 0;
                        existing.LastProposedAt = null;
                        (existing.ProposalText, existing.Rationale) =
                            await wording.WordAsync(place.Name, group.Key.Context, avg, count, ct);
                        created++;
                        continue;
                }
            }

            var (sentence, rationale) = await wording.WordAsync(place.Name, group.Key.Context, avg, count, ct);
            var habit = new Habit
            {
                Id = Guid.NewGuid(),
                PlaceId = group.Key.PlaceId,
                Context = group.Key.Context,
                State = HabitState.Proposed,
                AvgDurationMin = avg,
                VisitCount = count,
                ProposalText = sentence,
                Rationale = rationale,
            };
            db.Habits.Add(habit);
            habits.Add(habit);
            created++;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Habit inference: {Created} new proposal(s) from {Visits} visits", created, visits.Count);
        return created;
    }

    /// <summary>Derives a visit's context (design D5): weekday/bucket of the local arrival,
    /// direction from surrounding home visits (±3 h), kind from the nearest appointment
    /// starting within 2 h after arrival (null when there is none).</summary>
    public static ContextKey DeriveContext(
        Visit visit,
        IReadOnlyList<Visit> allVisitsOrdered,
        IReadOnlyList<Appointment> appointments,
        Guid? homePlaceId,
        TimeZoneInfo tz)
    {
        var localArrival = TimeZoneInfo.ConvertTime(visit.ArrivedAt, tz);

        var direction = TravelDirection.Other;
        if (homePlaceId is { } home && visit.PlaceId != home)
        {
            var homeAfter = allVisitsOrdered.Any(o => o.PlaceId == home
                && o.ArrivedAt >= visit.DepartedAt && o.ArrivedAt <= visit.DepartedAt + TimeSpan.FromHours(3));
            var homeBefore = allVisitsOrdered.Any(o => o.PlaceId == home && o.DepartedAt is not null
                && o.DepartedAt <= visit.ArrivedAt && o.DepartedAt >= visit.ArrivedAt - TimeSpan.FromHours(3));
            direction = homeAfter ? TravelDirection.TowardHome
                : homeBefore ? TravelDirection.AwayFromHome
                : TravelDirection.Other;
        }

        var adjacent = appointments
            .Where(a => a.Start >= visit.ArrivedAt && a.Start <= visit.ArrivedAt + TimeSpan.FromHours(2))
            .OrderBy(a => a.Start)
            .FirstOrDefault();

        return new ContextKey(
            localArrival.DayOfWeek,
            ContextKey.BucketOf(TimeOnly.FromDateTime(localArrival.DateTime)),
            direction,
            adjacent is null ? null : AppointmentKind.Classify(adjacent.Title));
    }

    private static double StdDev(IReadOnlyList<double> values)
    {
        var mean = values.Average();
        return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Count);
    }
}

/// <summary>Hangfire entry point: nightly at 03:00 local (and on demand via the dev endpoint).</summary>
public sealed class HabitInferenceJob(HabitInferenceService inference)
{
    public Task RunAsync(CancellationToken ct) => inference.RunAsync(ct);
}
