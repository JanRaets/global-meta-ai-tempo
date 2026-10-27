using System.Net;
using System.Text.Json;
using Lib.Net.Http.WebPush;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tempo.Core.Configuration;
using Tempo.Core.Data;
using Tempo.Core.Domain;
using Tempo.Core.Nudging;
using WirePushSubscription = Lib.Net.Http.WebPush.PushSubscription;

namespace Tempo.Integrations.Push;

/// <summary>
/// Web Push delivery (design D16, nudging spec "Web Push transport"): sends the nudge to
/// every stored subscription. 410/404 from a push service means the subscription is gone —
/// the row is deleted and that endpoint is not retried. Transient errors (429/5xx/network)
/// are retried up to 3 attempts with doubling backoff. Returns true when at least one
/// subscription accepted the message, so `Nudge.Status` records the real outcome.
/// </summary>
public sealed class WebPushTransport(
    TempoDbContext db,
    PushServiceClient client,
    IOptions<PushOptions> options,
    ILogger<WebPushTransport> logger) : INudgeTransport
{
    private const int MaxAttempts = 3;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<bool> SendAsync(Nudge nudge, CancellationToken ct = default)
    {
        var subscriptions = await db.PushSubscriptions.ToListAsync(ct);
        if (subscriptions.Count == 0)
        {
            logger.LogWarning("No push subscriptions — nudge [{Kind}] not delivered: {Text}", nudge.Kind, nudge.Text);
            return false;
        }

        var message = new PushMessage(PayloadFor(nudge))
        {
            Urgency = nudge.Kind == NudgeKind.HabitProposal ? PushMessageUrgency.Normal : PushMessageUrgency.High,
        };

        var delivered = 0;
        foreach (var subscription in subscriptions)
        {
            if (await SendToAsync(subscription, message, ct))
                delivered++;
        }
        await db.SaveChangesAsync(ct); // persist any 410/404 deletions
        logger.LogInformation("Push [{Kind}] delivered to {Delivered}/{Total} subscription(s)",
            nudge.Kind, delivered, subscriptions.Count);
        return delivered > 0;
    }

    private async Task<bool> SendToAsync(Tempo.Core.Domain.PushSubscription subscription, PushMessage message, CancellationToken ct)
    {
        var wire = new WirePushSubscription
        {
            Endpoint = subscription.Endpoint,
            Keys = new Dictionary<string, string> { ["p256dh"] = subscription.P256dh, ["auth"] = subscription.Auth },
        };
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await client.RequestPushMessageDeliveryAsync(wire, message, ct);
                return true;
            }
            catch (PushServiceClientException ex) when (ex.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
            {
                db.PushSubscriptions.Remove(subscription);
                logger.LogInformation("Push subscription expired ({Status}) — deleted: {Endpoint}", (int)ex.StatusCode, subscription.Endpoint);
                return false;
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < MaxAttempts)
            {
                var delay = TimeSpan.FromMilliseconds(options.Value.RetryBaseMs * Math.Pow(2, attempt - 1));
                logger.LogWarning("Push attempt {Attempt}/{Max} failed ({Message}); retrying in {Delay}",
                    attempt, MaxAttempts, ex.Message, delay);
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, ct);
            }
            catch (Exception ex) when (ex is PushServiceClientException or HttpRequestException)
            {
                logger.LogWarning("Push to {Endpoint} failed: {Message}", subscription.Endpoint, ex.Message);
                return false;
            }
        }
        return false;
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        PushServiceClientException p => p.StatusCode == HttpStatusCode.TooManyRequests || (int)p.StatusCode >= 500,
        HttpRequestException => true,
        _ => false,
    };

    /// <summary>Payload per D16: {kind, title, body, url, nudgeId|habitId}. Leave/Late deep-link
    /// to /now, habit proposals to /habits; a replacement presents itself as an update.
    /// Public because the wire body is encrypted (aes128gcm) — tests assert on this instead.</summary>
    public static string PayloadFor(Nudge nudge) => JsonSerializer.Serialize(new
    {
        kind = nudge.Kind.ToString(),
        title = nudge.Kind switch
        {
            _ when nudge.ReplacesNudgeId is not null => "TEMPO — updated plan",
            NudgeKind.Leave => "Time to leave",
            NudgeKind.Late => "Running late",
            _ => "TEMPO noticed something",
        },
        body = nudge.Text,
        url = nudge.Kind == NudgeKind.HabitProposal ? "/habits" : "/now",
        nudgeId = nudge.Id,
        habitId = nudge.HabitId,
        // Same tag per plan: the OS replaces the old notification instead of stacking them.
        tag = nudge.Kind == NudgeKind.HabitProposal ? nudge.Id : nudge.PlanId,
    }, Json);
}
