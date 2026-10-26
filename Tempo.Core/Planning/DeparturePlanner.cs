using Microsoft.Extensions.Logging;
using Tempo.Core.Domain;
using Tempo.Core.Geo;

namespace Tempo.Core.Planning;

/// <summary>
/// Pure departure arithmetic (design D6). Only deterministic code: routing durations come
/// from IRoutingClient, dwell times from confirmed habits; no model output enters here.
/// Accepts only IReadOnlyList&lt;ConfirmedHabit&gt; (design D3).
/// </summary>
public sealed class DeparturePlanner(IRoutingClient routing, ILogger<DeparturePlanner> logger)
{
    public async Task<PlanOptionsResult> Plan(
        Appointment appointment,
        GeoPoint origin,
        GeoPoint destination,
        IReadOnlyList<ConfirmedHabit> habits,
        IReadOnlyList<PlannerStop> extraStops,
        IReadOnlyList<TravelMode> modes,
        DateTimeOffset effectiveNow,
        CancellationToken ct = default)
    {
        var options = new List<PlanOption>();
        var skippedModes = new List<string>();

        foreach (var mode in modes)
        {
            TimeSpan baseTravel;
            try
            {
                baseTravel = await routing.EstimateAsync(origin, destination, mode, effectiveNow, ct);
            }
            catch (RoutingUnavailableException ex)
            {
                logger.LogWarning("Routing unavailable for {Mode}, mode skipped: {Message}", mode, ex.Message);
                skippedModes.Add(mode.ToString());
                continue;
            }

            foreach (var subset in StopSubsets(habits))
            {
                var stops = subset
                    .Select(h => new PlannerStop(h.PlaceId, h.PlaceName, (int)Math.Round(h.AvgDurationMin), new GeoPoint(h.Lat, h.Lng)))
                    .Concat(extraStops)
                    .ToList();
                TimeSpan travelWithStops;
                if (stops.Count == 0)
                {
                    travelWithStops = baseTravel;
                }
                else
                {
                    try
                    {
                        var detourTotal = await routing.DetourAsync(origin, stops.Select(s => s.Point).ToList(), destination, mode, ct);
                        travelWithStops = detourTotal > baseTravel ? detourTotal : baseTravel;
                    }
                    catch (RoutingUnavailableException ex)
                    {
                        logger.LogWarning("Detour routing unavailable for {Mode}, stop subset skipped: {Message}", mode, ex.Message);
                        continue;
                    }
                }

                var stopTime = TimeSpan.FromMinutes(stops.Sum(s => s.DurationMin));
                var total = travelWithStops + stopTime;
                var leaveAt = appointment.Start - TimeSpan.FromMinutes(appointment.BufferMin) - total;
                var slackMin = (int)Math.Round((leaveAt - effectiveNow).TotalMinutes);
                var departAt = leaveAt >= effectiveNow ? leaveAt : effectiveNow;
                options.Add(new PlanOption(
                    OptionId: Guid.NewGuid().ToString("N")[..8],
                    Mode: mode,
                    Stops: stops.Select(s => new PlanStop(s.PlaceId, s.Name, s.DurationMin)).ToList(),
                    LeaveAt: leaveAt,
                    ArriveAt: departAt + total,
                    IsFeasible: leaveAt >= effectiveNow,
                    SlackMin: slackMin));
            }
        }

        return new PlanOptionsResult(options,
            skippedModes.Count == 0 ? null : $"routing unavailable for: {string.Join(", ", skippedModes)}");
    }

    /// <summary>All matching habits, each one skipped, none (departure-planning spec) — not the full powerset.</summary>
    private static IEnumerable<IReadOnlyList<ConfirmedHabit>> StopSubsets(IReadOnlyList<ConfirmedHabit> habits)
    {
        yield return habits;
        if (habits.Count == 0)
            yield break;
        if (habits.Count > 1)
        {
            foreach (var skipped in habits)
                yield return habits.Where(h => h != skipped).ToList();
        }
        yield return [];
    }
}
