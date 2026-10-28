using Microsoft.Extensions.Logging;
using Tempo.Core.Domain;
using Tempo.Core.Geo;

namespace Tempo.Core.Planning;

/// <summary>
/// Pure departure arithmetic (design D6). Only deterministic code: routing durations come
/// from IRoutingClient, dwell times from confirmed habits; no model output enters here.
/// Accepts only IReadOnlyList&lt;ConfirmedHabit&gt; (design D3).
/// </summary>
public sealed class DeparturePlanner(
    IRoutingClient routing,
    ILogger<DeparturePlanner> logger,
    Tempo.Core.Configuration.RoutingOptions? routingOptions = null)
{
    private readonly Tempo.Core.Configuration.RoutingOptions options = routingOptions ?? new();

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
        var (real, skippedModes) = await BuildOptionsAsync(
            routing, estimated: false, appointment, origin, destination, habits, extraStops, modes, effectiveNow, ct);

        // D18: only when NO mode could be routed does the straight-line estimate step in —
        // partial availability keeps real options only. Estimated options are flagged and
        // the note lands in the trace ("routing unavailable, used estimate").
        if (real.Count == 0 && skippedModes.Count > 0 && options.FallbackEnabled)
        {
            logger.LogWarning("Routing unavailable for every mode — falling back to the straight-line estimate (D18)");
            var (estimatedOptions, _) = await BuildOptionsAsync(
                new StraightLineRouting(), estimated: true, appointment, origin, destination, habits, extraStops, modes, effectiveNow, ct);
            return new PlanOptionsResult(estimatedOptions, "routing unavailable, used estimate");
        }

        return new PlanOptionsResult(real,
            skippedModes.Count == 0 ? null : $"routing unavailable for: {string.Join(", ", skippedModes)}");
    }

    private async Task<(List<PlanOption> Options, List<string> SkippedModes)> BuildOptionsAsync(
        IRoutingClient router,
        bool estimated,
        Appointment appointment,
        GeoPoint origin,
        GeoPoint destination,
        IReadOnlyList<ConfirmedHabit> habits,
        IReadOnlyList<PlannerStop> extraStops,
        IReadOnlyList<TravelMode> modes,
        DateTimeOffset effectiveNow,
        CancellationToken ct)
    {
        var options = new List<PlanOption>();
        var skippedModes = new List<string>();

        foreach (var mode in modes)
        {
            TimeSpan baseTravel;
            try
            {
                baseTravel = await router.EstimateAsync(origin, destination, mode, effectiveNow, ct);
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
                        var detourTotal = await router.DetourAsync(origin, stops.Select(s => s.Point).ToList(), destination, mode, ct);
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
                // The buffer is a margin, not a deadline (D6, 28 Oct 2026): leaveAt is the
                // buffered target, leaveBy the last moment that still arrives at start.
                // Feasibility and slack are measured against leaveBy; the buffer plays no part.
                var leaveAt = appointment.Start - TimeSpan.FromMinutes(appointment.BufferMin) - total;
                var leaveBy = appointment.Start - total;
                var departAt = leaveAt >= effectiveNow ? leaveAt : effectiveNow;
                var arriveAt = departAt + total;
                options.Add(new PlanOption(
                    OptionId: Guid.NewGuid().ToString("N")[..8],
                    Mode: mode,
                    Stops: stops.Select(s => new PlanStop(s.PlaceId, s.Name, s.DurationMin)).ToList(),
                    LeaveAt: leaveAt,
                    LeaveBy: leaveBy,
                    ArriveAt: arriveAt,
                    IsFeasible: arriveAt <= appointment.Start,
                    SlackMin: (int)Math.Round((leaveBy - effectiveNow).TotalMinutes),
                    IsEstimated: estimated));
            }
        }

        return (options, skippedModes);
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
