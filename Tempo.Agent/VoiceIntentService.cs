using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tempo.Core.Data;
using Tempo.Core.Domain;
using Tempo.Core.Events;
using Tempo.Core.Models;
using Tempo.Core.Planning;

namespace Tempo.Agent;

/// <summary>What the model returns: an intent name plus the words it heard. Ids are never
/// model output — stopName/placeName/habitName are resolved to ids by lookup here (D1).</summary>
public sealed class IntentClassification
{
    public string Type { get; set; } = "Unknown";
    public string? StopName { get; set; }
    public string? PlaceName { get; set; }
    public int? Minutes { get; set; }
    public string? Mode { get; set; }
    public string? HabitName { get; set; }
}

public sealed record VoiceResult(
    Guid? IntentId,
    string Transcript,
    string Kind,
    bool Applied,
    string Change,
    IReadOnlyList<string> Suggestions)
{
    public static VoiceResult NotApplied(string transcript, string kind, string change, Guid? intentId = null) =>
        new(intentId, transcript, kind, false, change, []);
}

/// <summary>
/// Voice/text input (design D9, voice-input spec): transcribe, classify into the closed
/// intent set, apply as a per-appointment PlanInputs change only (never habits or calendar —
/// except ConfirmHabit, which routes through ConfirmHabitHandler like every confirmation),
/// then wake the agent via the IntentApplied event. Undo reverts the change and re-runs.
/// </summary>
public sealed class VoiceIntentService(
    TempoDbContext db,
    IMetaModels models,
    PlanService plans,
    ConfirmHabitHandler confirmHandler,
    IDomainEvents events,
    TimeProvider clock,
    ILogger<VoiceIntentService> logger)
{
    /// <summary>15 s of 24 kHz mono 16-bit PCM plus header, rounded up.</summary>
    public const int MaxAudioBytes = 800_000;

    public static readonly string[] UnknownSuggestions = ["SkipStop", "AddStop", "RunningLate"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly Lazy<string> SystemPrompt = new(() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Prompts", "intent.system.md")));

    // Strict-mode schema (additionalProperties:false + all required ⇒ strict:true on the
    // wire): the lax mode occasionally corrupted a property key, strict decoding never does.
    private const string Schema = """
        {"type":"object","additionalProperties":false,"properties":{
          "type":{"type":"string","enum":["SkipStop","AddStop","RunningLate","LeavingNow","PinMode","ConfirmHabit","Unknown"]},
          "stopName":{"type":["string","null"]},
          "placeName":{"type":["string","null"]},
          "minutes":{"type":["integer","null"]},
          "mode":{"type":["string","null"]},
          "habitName":{"type":["string","null"]}},
         "required":["type","stopName","placeName","minutes","mode","habitName"]}
        """;

    public async Task<VoiceResult> HandleAudioAsync(ReadOnlyMemory<byte> wav, CancellationToken ct = default)
    {
        if (wav.Length > MaxAudioBytes)
            return VoiceResult.NotApplied("", "Unknown", "Recording too long — keep it under 15 seconds.");
        var transcription = await models.TranscribeAsync(wav, "audio/wav", ct);
        if (string.IsNullOrWhiteSpace(transcription.Text))
            return VoiceResult.NotApplied("", "Unknown", "I didn't catch that.");
        return await HandleTextAsync(transcription.Text.Trim(), ct);
    }

    public async Task<VoiceResult> HandleTextAsync(string transcript, CancellationToken ct = default)
    {
        var appointment = await NextPlannableAppointmentAsync(ct);
        var plan = appointment is null ? null : await plans.CurrentAsync(appointment.Id, ct);
        var stopCandidates = plan?.Stops.Select(s => s.Name).ToList() ?? [];
        var habitCandidates = await db.Habits.AsNoTracking().Include(h => h.Place)
            .Where(h => h.State == HabitState.Proposed)
            .Select(h => h.Place!.Name)
            .ToListAsync(ct);

        IntentClassification classification;
        try
        {
            var context = JsonSerializer.Serialize(new
            {
                utterance = transcript,
                stopCandidates,
                habitCandidates,
            }, Json);
            var result = await models.ChatJsonAsync<IntentClassification>(
                [ChatMessage.System(SystemPrompt.Value), ChatMessage.User(context)],
                Schema, purpose: "intent-classify", ct);
            classification = result.Value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Intent classification unavailable: {Message}", ex.Message);
            classification = new IntentClassification(); // Unknown — the chips still work
        }

        return await ApplyAsync(transcript, classification, appointment, plan, ct);
    }

    private async Task<VoiceResult> ApplyAsync(
        string transcript, IntentClassification c, Appointment? appointment, DeparturePlan? plan, CancellationToken ct)
    {
        if (!Enum.TryParse<IntentType>(c.Type, ignoreCase: true, out var type))
            type = IntentType.Unknown;

        // Everything except ConfirmHabit and Unknown needs an appointment to apply to.
        if (appointment is null && type is not (IntentType.ConfirmHabit or IntentType.Unknown))
            return VoiceResult.NotApplied(transcript, type.ToString(), "No upcoming appointment to plan for.");

        var (applied, change, undo) = type switch
        {
            IntentType.SkipStop => await ApplySkipStopAsync(c, appointment!, plan, ct),
            IntentType.AddStop => await ApplyAddStopAsync(c, appointment!, ct),
            IntentType.RunningLate => await ApplyRunningLateAsync(c, appointment!, ct),
            IntentType.LeavingNow => (true, "Got it — planning from now.", new UndoInfo()),
            IntentType.PinMode => await ApplyPinModeAsync(c, appointment!, ct),
            IntentType.ConfirmHabit => await ApplyConfirmHabitAsync(c, ct),
            _ => (false, "I didn't recognise that as a plan change.", new UndoInfo()),
        };

        var intent = new Intent
        {
            Id = Guid.NewGuid(),
            Type = type,
            AppointmentId = appointment?.Id,
            Transcript = transcript,
            ArgsJson = JsonSerializer.Serialize(new { classification = c, undo }, Json),
        };
        db.Intents.Add(intent);
        await db.SaveChangesAsync(ct);

        if (applied && type != IntentType.ConfirmHabit) // ConfirmHabitHandler already published HabitConfirmed
            await events.PublishAsync(new IntentApplied(intent.Id, type.ToString(), appointment?.Id), ct);

        var suggestions = type == IntentType.Unknown ? UnknownSuggestions : [];
        return new VoiceResult(intent.Id, transcript, type.ToString(), applied, change, suggestions);
    }

    private async Task<(bool, string, UndoInfo)> ApplySkipStopAsync(
        IntentClassification c, Appointment appointment, DeparturePlan? plan, CancellationToken ct)
    {
        var stops = plan?.Stops ?? [];
        var match = stops.FirstOrDefault(s => NamesMatch(s.Name, c.StopName))
            ?? (stops.Count == 1 ? stops[0] : null);
        if (match is null)
            return (false, stops.Count == 0 ? "The current plan has no stops to skip." : "Couldn't tell which stop to skip.", new UndoInfo());
        var inputs = await InputsAsync(appointment.Id, ct);
        if (!inputs.SkippedPlaceIds.Contains(match.PlaceId))
            inputs.SkippedPlaceIds.Add(match.PlaceId);
        inputs.UpdatedAt = clock.GetUtcNow();
        return (true, $"Skipping {match.Name} for {appointment.Title}.", new UndoInfo { SkippedPlaceId = match.PlaceId });
    }

    private async Task<(bool, string, UndoInfo)> ApplyAddStopAsync(
        IntentClassification c, Appointment appointment, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(c.PlaceName))
            return (false, "Couldn't tell which place to add.", new UndoInfo());
        var place = (await db.Places.AsNoTracking().ToListAsync(ct))
            .FirstOrDefault(p => NamesMatch(p.Name, c.PlaceName));
        if (place is null)
            return (false, $"I don't know \"{c.PlaceName}\" yet.", new UndoInfo());
        var minutes = Math.Clamp(c.Minutes ?? 10, 1, 60);
        var inputs = await InputsAsync(appointment.Id, ct);
        if (!inputs.ExtraStops.Any(s => s.PlaceId == place.Id))
            inputs.ExtraStops.Add(new ExtraStop(place.Id, place.Name, minutes));
        inputs.UpdatedAt = clock.GetUtcNow();
        return (true, $"Adding a stop at {place.Name} (~{minutes} min).", new UndoInfo { ExtraPlaceId = place.Id });
    }

    private async Task<(bool, string, UndoInfo)> ApplyRunningLateAsync(
        IntentClassification c, Appointment appointment, CancellationToken ct)
    {
        var minutes = Math.Clamp(c.Minutes ?? 10, 1, 120);
        var inputs = await InputsAsync(appointment.Id, ct);
        var undo = new UndoInfo { PreviousLateOffsetMin = inputs.LateOffsetMin };
        inputs.LateOffsetMin = minutes;
        inputs.UpdatedAt = clock.GetUtcNow();
        return (true, $"Planning with a {minutes}-minute delay.", undo);
    }

    private async Task<(bool, string, UndoInfo)> ApplyPinModeAsync(
        IntentClassification c, Appointment appointment, CancellationToken ct)
    {
        if (ParseMode(c.Mode) is not { } mode)
            return (false, "Couldn't tell which travel mode you meant.", new UndoInfo());
        var inputs = await InputsAsync(appointment.Id, ct);
        var undo = new UndoInfo { PreviousPinnedMode = inputs.PinnedMode, HadPinnedMode = true };
        inputs.PinnedMode = mode;
        inputs.UpdatedAt = clock.GetUtcNow();
        return (true, $"Pinned {mode.ToString().ToLowerInvariant()} for this trip.", undo);
    }

    private async Task<(bool, string, UndoInfo)> ApplyConfirmHabitAsync(IntentClassification c, CancellationToken ct)
    {
        var proposed = await db.Habits.Include(h => h.Place)
            .Where(h => h.State == HabitState.Proposed)
            .ToListAsync(ct);
        var habit = proposed.FirstOrDefault(h => NamesMatch(h.Place!.Name, c.HabitName))
            ?? (proposed.Count == 1 ? proposed[0] : null);
        if (habit is null)
            return (false, proposed.Count == 0 ? "There is no proposed habit to confirm." : "Couldn't tell which habit to confirm.", new UndoInfo());
        var confirmed = await confirmHandler.ConfirmAsync(habit.Id, ct);
        return (true, $"Confirmed: {confirmed.PlaceName}.", new UndoInfo { ConfirmedHabitId = habit.Id });
    }

    public async Task<VoiceResult?> UndoAsync(Guid intentId, CancellationToken ct = default)
    {
        var intent = await db.Intents.SingleOrDefaultAsync(i => i.Id == intentId, ct);
        if (intent is null || intent.UndoneAt is not null)
            return null;
        var undo = JsonDocument.Parse(intent.ArgsJson).RootElement.TryGetProperty("undo", out var u)
            ? u.Deserialize<UndoInfo>(Json) ?? new UndoInfo()
            : new UndoInfo();

        var inputs = intent.AppointmentId is { } appointmentId ? await InputsAsync(appointmentId, ct) : null;
        if (inputs is not null)
        {
            if (undo.SkippedPlaceId is { } skipped)
                inputs.SkippedPlaceIds.Remove(skipped);
            if (undo.ExtraPlaceId is { } extra)
                inputs.ExtraStops.RemoveAll(s => s.PlaceId == extra);
            if (undo.PreviousLateOffsetMin is { } prevLate)
                inputs.LateOffsetMin = prevLate;
            if (undo.HadPinnedMode)
                inputs.PinnedMode = undo.PreviousPinnedMode;
            inputs.UpdatedAt = clock.GetUtcNow();
        }
        if (undo.ConfirmedHabitId is { } habitId
            && await db.Habits.SingleOrDefaultAsync(h => h.Id == habitId, ct) is { } habit)
        {
            habit.State = HabitState.Proposed; // back to proposed; a real rejection is its own action
            habit.DecidedAt = null;
        }

        intent.UndoneAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await events.PublishAsync(new IntentApplied(intent.Id, "undo", intent.AppointmentId), ct);
        return new VoiceResult(intent.Id, intent.Transcript ?? "", intent.Type.ToString(), true, "Undone.", []);
    }

    /// <summary>Per-appointment planner inputs row, created on first change (section 6).</summary>
    private async Task<PlanInputs> InputsAsync(Guid appointmentId, CancellationToken ct)
    {
        var inputs = await db.PlanInputs.SingleOrDefaultAsync(p => p.AppointmentId == appointmentId, ct);
        if (inputs is null)
        {
            inputs = new PlanInputs { Id = Guid.NewGuid(), AppointmentId = appointmentId };
            db.PlanInputs.Add(inputs);
        }
        return inputs;
    }

    private async Task<Appointment?> NextPlannableAppointmentAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return (await db.Appointments.AsNoTracking().Include(a => a.Place).ToListAsync(ct))
            .Where(a => a.PlaceId != null && !a.IsVirtual && a.Start > now && a.Start <= now.AddHours(6))
            .OrderBy(a => a.Start)
            .FirstOrDefault();
    }

    private static bool NamesMatch(string candidate, string? spoken) =>
        !string.IsNullOrWhiteSpace(spoken)
        && (candidate.Contains(spoken, StringComparison.OrdinalIgnoreCase)
            || spoken.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static TravelMode? ParseMode(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        "bicycle" or "bike" or "fiets" or "cycling" => TravelMode.Bicycle,
        "walking" or "walk" or "foot" or "lopen" or "te voet" => TravelMode.Walking,
        "car" or "auto" or "drive" or "driving" => TravelMode.Car,
        "train" or "trein" => TravelMode.Train,
        _ => null,
    };

    /// <summary>Stored inside Intent.ArgsJson so Undo knows exactly what to revert.</summary>
    public sealed class UndoInfo
    {
        public Guid? SkippedPlaceId { get; set; }
        public Guid? ExtraPlaceId { get; set; }
        public int? PreviousLateOffsetMin { get; set; }
        public TravelMode? PreviousPinnedMode { get; set; }
        public bool HadPinnedMode { get; set; }
        public Guid? ConfirmedHabitId { get; set; }
    }
}
