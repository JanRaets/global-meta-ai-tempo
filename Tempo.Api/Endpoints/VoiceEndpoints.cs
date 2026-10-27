using FastEndpoints;
using Tempo.Agent;

namespace Tempo.Api.Endpoints;

/// <summary>POST /api/voice — raw WAV body (mono PCM 16/24 kHz, ≤15 s; design D9).
/// First endpoint under /api: section 12 moves the remaining JSON routes there too.</summary>
public sealed class VoiceEndpoint(VoiceIntentService voice) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/api/voice");
        AllowAnonymous(); // raw audio/wav body, read straight from the request stream
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await HttpContext.Request.Body.CopyToAsync(buffer, ct);
        if (buffer.Length == 0)
        {
            await Send.StringAsync("empty audio body", 400, cancellation: ct);
            return;
        }
        if (buffer.Length > VoiceIntentService.MaxAudioBytes)
        {
            await Send.StringAsync("audio too long (max 15 s)", 400, cancellation: ct);
            return;
        }
        var result = await voice.HandleAudioAsync(buffer.ToArray(), ct);
        await Send.OkAsync(result, ct);
    }
}

public sealed class VoiceTextRequest
{
    public required string Text { get; set; }
}

/// <summary>Text fallback for when the microphone is denied (voice-input spec) — same
/// classify/apply pipeline, no transcription.</summary>
public sealed class VoiceTextEndpoint(VoiceIntentService voice) : Endpoint<VoiceTextRequest>
{
    public override void Configure()
    {
        Post("/api/voice/text");
        AllowAnonymous();
    }

    public override async Task HandleAsync(VoiceTextRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Text))
        {
            await Send.StringAsync("empty text", 400, cancellation: ct);
            return;
        }
        await Send.OkAsync(await voice.HandleTextAsync(req.Text.Trim(), ct), ct);
    }
}

public sealed class VoiceUndoEndpoint(VoiceIntentService voice) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/api/voice/undo/{id}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var result = await voice.UndoAsync(Route<Guid>("id"), ct);
        if (result is null)
        {
            await Send.StringAsync("nothing to undo", 404, cancellation: ct);
            return;
        }
        await Send.OkAsync(result, ct);
    }
}
