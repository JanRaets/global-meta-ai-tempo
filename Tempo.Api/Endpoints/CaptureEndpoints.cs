using FastEndpoints;
using Tempo.Agent;
using Tempo.Core.Models;

namespace Tempo.Api.Endpoints;

/// <summary>POST /api/capture — raw JPEG/PNG body (client-side resized to ≤1600 px),
/// read by Spark vision into a confirm card (capture-appointment spec).</summary>
public sealed class CaptureEndpoint(CaptureService capture) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/api/capture");
        AllowAnonymous(); // raw image body, read straight from the request stream
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await HttpContext.Request.Body.CopyToAsync(buffer, ct);
        try
        {
            var result = await capture.CaptureAsync(
                buffer.ToArray(), HttpContext.Request.ContentType ?? "", ct);
            await Send.OkAsync(result, ct);
        }
        catch (CaptureValidationException ex)
        {
            await Send.StringAsync(ex.Message, 400, cancellation: ct);
        }
        catch (Exception ex) when (ex is ModelUnavailableException or ModelOutputException)
        {
            await Send.StringAsync("Couldn't read the photo — try again.", 502, cancellation: ct);
        }
    }
}

public sealed class ConfirmCaptureRequest
{
    public string Title { get; set; } = "";
    /// <summary>From the card's datetime-local input: "yyyy-MM-ddTHH:mm" in the user timezone.</summary>
    public string StartLocal { get; set; } = "";
    public string? PlaceText { get; set; }
}

/// <summary>POST /api/capture/confirm — the card's Add button. Creates the captured
/// appointment; the agent plans for it via the AppointmentChanged event.</summary>
public sealed class ConfirmCaptureEndpoint(CaptureService capture) : Endpoint<ConfirmCaptureRequest>
{
    public override void Configure()
    {
        Post("/api/capture/confirm");
        AllowAnonymous();
    }

    public override async Task HandleAsync(ConfirmCaptureRequest req, CancellationToken ct)
    {
        try
        {
            await Send.OkAsync(
                await capture.ConfirmAsync(new ConfirmCapture(req.Title, req.StartLocal, req.PlaceText), ct), ct);
        }
        catch (CaptureValidationException ex)
        {
            await Send.StringAsync(ex.Message, 400, cancellation: ct);
        }
    }
}
