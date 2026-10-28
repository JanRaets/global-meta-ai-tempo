using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tempo.Core.Data;
using Tempo.Core.Domain;
using Tempo.Core.Events;
using Tempo.Core.Geo;

namespace Tempo.Jobs;

/// <summary>One-off after the ranked-geocoding deploy (28 Oct 2026): re-resolve the next
/// 7 days of appointments so earlier mis-geocodes (the wrong hockey club) merge onto the
/// right places. Idempotent and cheap on re-runs: a location text that already has a
/// resolution log under the new ranking — or an override — is skipped.</summary>
public sealed class ReResolvePlacesJob(
    TempoDbContext db,
    PlaceResolver places,
    IDomainEvents events,
    TimeProvider clock,
    ILogger<ReResolvePlacesJob> logger)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var candidates = (await db.Appointments
                .Where(a => a.RawLocation != null && !a.IsVirtual)
                .ToListAsync(ct))
            .Where(a => a.Start >= now && a.Start <= now.AddDays(7))
            .ToList();
        if (candidates.Count == 0)
            return;

        var logged = (await db.PlaceResolutionLogs.AsNoTracking().Select(l => l.RawText).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var overridden = (await db.PlaceOverrides.AsNoTracking().Select(o => o.RawText).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var group in candidates.GroupBy(a => a.RawLocation!.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            if (logged.Contains(group.Key) || overridden.Contains(group.Key))
                continue; // already resolved under the new ranking (or corrected by the user)
            var placeId = await places.ResolveAsync(group.Key, ct);
            if (placeId is null)
                continue;
            foreach (var appointment in group.Where(a => a.PlaceId != placeId))
            {
                logger.LogInformation("Re-resolve: '{Title}' moves to the ranked place for '{Raw}'",
                    appointment.Title, group.Key);
                appointment.PlaceId = placeId;
                await db.SaveChangesAsync(ct);
                await events.PublishAsync(new AppointmentChanged(appointment.Id, AppointmentChangeKind.Updated), ct);
            }
            await db.SaveChangesAsync(ct); // persist the resolution log even without moves
        }
    }
}
