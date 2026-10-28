using System.Text.Json;
using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Tempo.Core.Data;
using Tempo.Core.Domain;
using Tempo.Core.Events;
using Tempo.Core.Geo;

namespace Tempo.Api.Endpoints;

/// <summary>Place correction endpoints (geocoding amendment, 28 Oct 2026): geocoding will
/// be wrong again, so the Today row's place line opens a sheet with the logged candidates;
/// picking one re-points the appointment, replans, and stores a sticky override.</summary>
public sealed class PlaceCandidatesEndpoint(TempoDbContext db, PlaceResolver places) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/api/appointments/{id}/place-candidates");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var id = Route<Guid>("id");
        var appointment = await db.Appointments.AsNoTracking().Include(a => a.Place)
            .SingleOrDefaultAsync(a => a.Id == id, ct);
        if (appointment is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var raw = appointment.RawLocation?.Trim();
        object[] candidates = [];
        if (raw is { Length: > 0 })
        {
            var logRow = await db.PlaceResolutionLogs.AsNoTracking()
                .Where(l => l.RawText == raw).FirstOrDefaultAsync(ct);
            var list = logRow is not null
                ? JsonSerializer.Deserialize<List<JsonElement>>(logRow.CandidatesJson) ?? []
                : [];
            candidates = list.Count > 0
                ? list.Take(3).Select(c => (object)new
                {
                    name = c.GetProperty("name").GetString(),
                    label = c.GetProperty("label").GetString(),
                    locality = c.TryGetProperty("locality", out var l) ? l.GetString() : null,
                    lat = c.GetProperty("lat").GetDouble(),
                    lng = c.GetProperty("lng").GetDouble(),
                }).ToArray()
                : (await places.SearchRankedAsync(raw, ct)).Take(3)
                    .Select(c => (object)new { name = c.Name, label = c.Label, locality = c.Locality, lat = c.Lat, lng = c.Lng })
                    .ToArray();
        }
        await Send.OkAsync(new
        {
            rawText = raw,
            current = appointment.Place is null ? null : new { id = appointment.Place.Id, name = appointment.Place.Name },
            candidates,
        }, ct);
    }
}

/// <summary>GET /api/places/search?q= — ranked candidates for the sheet's free-text search.</summary>
public sealed class PlaceSearchEndpoint(PlaceResolver places) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/api/places/search");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var q = Query<string>("q") ?? "";
        if (string.IsNullOrWhiteSpace(q))
        {
            await Send.OkAsync(Array.Empty<object>(), ct);
            return;
        }
        var ranked = await places.SearchRankedAsync(q, ct);
        await Send.OkAsync(ranked.Take(5)
            .Select(c => new { name = c.Name, label = c.Label, locality = c.Locality, lat = c.Lat, lng = c.Lng }), ct);
    }
}

public sealed class SetAppointmentPlaceRequest
{
    public double Lat { get; set; }
    public double Lng { get; set; }
    public string? Name { get; set; }
}

/// <summary>POST /api/appointments/{id}/place — the sheet's pick: re-points the appointment,
/// stores the override for every future event with the same text, and replans.</summary>
public sealed class SetAppointmentPlaceEndpoint(TempoDbContext db, PlaceResolver places, IDomainEvents events)
    : Endpoint<SetAppointmentPlaceRequest>
{
    public override void Configure()
    {
        Post("/api/appointments/{id}/place");
        AllowAnonymous();
    }

    public override async Task HandleAsync(SetAppointmentPlaceRequest req, CancellationToken ct)
    {
        var id = Route<Guid>("id");
        var appointment = await db.Appointments.SingleOrDefaultAsync(a => a.Id == id, ct);
        if (appointment is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        var place = await places.ResolvePointAsync(req.Lat, req.Lng, req.Name ?? "Corrected place", ct);
        await db.SaveChangesAsync(ct); // place id is needed for the override row
        appointment.PlaceId = place.Id;
        appointment.IsVirtual = false;

        if (appointment.RawLocation?.Trim() is { Length: > 0 } raw)
        {
            var existing = (await db.PlaceOverrides.ToListAsync(ct))
                .FirstOrDefault(o => o.RawText.Equals(raw, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
                db.PlaceOverrides.Add(new PlaceOverride { Id = Guid.NewGuid(), RawText = raw, PlaceId = place.Id });
            else
                existing.PlaceId = place.Id;
        }
        await db.SaveChangesAsync(ct);
        await events.PublishAsync(new AppointmentChanged(appointment.Id, AppointmentChangeKind.Updated), ct);
        await Send.OkAsync(new { placeId = place.Id, name = place.Name }, ct);
    }
}

/// <summary>GET /api/place-overrides — Settings' "Place corrections" list.</summary>
public sealed class PlaceOverridesEndpoint(TempoDbContext db) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/api/place-overrides");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var overrides = await db.PlaceOverrides.AsNoTracking().ToListAsync(ct);
        var placeNames = await db.Places.AsNoTracking()
            .Where(p => overrides.Select(o => o.PlaceId).Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        await Send.OkAsync(overrides
            .OrderBy(o => o.RawText)
            .Select(o => new
            {
                id = o.Id,
                rawText = o.RawText,
                placeName = placeNames.GetValueOrDefault(o.PlaceId, "(removed place)"),
            }), ct);
    }
}

/// <summary>DELETE /api/place-overrides/{id} — remove a correction; geocoding takes over again.</summary>
public sealed class DeletePlaceOverrideEndpoint(TempoDbContext db) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("/api/place-overrides/{id}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var id = Route<Guid>("id");
        var row = await db.PlaceOverrides.SingleOrDefaultAsync(o => o.Id == id, ct);
        if (row is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }
        db.PlaceOverrides.Remove(row);
        await db.SaveChangesAsync(ct);
        await Send.OkAsync(new { removed = true }, ct);
    }
}
