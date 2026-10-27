using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tempo.Core.Configuration;
using Tempo.Core.Domain;
using Tempo.Integrations.Push;
using Tempo.Tests.Fakes;

namespace Tempo.Tests.Push;

/// <summary>Nudging spec "Web Push transport": payload shape, 410 cleanup, 3× retry.</summary>
public sealed class WebPushTransportTests : IDisposable
{
    private readonly TestDb db = new();
    private static readonly (string PublicKey, string PrivateKey) Vapid = VapidKeyGenerator.Generate();

    public WebPushTransportTests()
    {
        db.Context.Add(new UserSettings { Id = Guid.NewGuid(), LocationApiKey = "k" });
        db.Context.SaveChanges();
    }

    private WebPushTransport Transport(StubHttpHandler stub) => new(
        db.Context,
        new PushServiceClient(new HttpClient(stub))
        {
            DefaultAuthentication = new VapidAuthentication(Vapid.PublicKey, Vapid.PrivateKey) { Subject = "mailto:test@example.org" },
        },
        Options.Create(new PushOptions { RetryBaseMs = 0 }),
        NullLogger<WebPushTransport>.Instance);

    /// <summary>A browser-shaped subscription: p256dh must be a real P-256 point or encryption fails.</summary>
    private Tempo.Core.Domain.PushSubscription AddSubscription(string endpoint = "https://push.example.org/sub-1")
    {
        var sub = new Tempo.Core.Domain.PushSubscription
        {
            Id = Guid.NewGuid(),
            Endpoint = endpoint,
            P256dh = VapidKeyGenerator.Generate().PublicKey,
            Auth = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
        };
        db.Context.PushSubscriptions.Add(sub);
        db.Context.SaveChanges();
        return sub;
    }

    private static Nudge LeaveNudge(Guid? replaces = null, NudgeKind kind = NudgeKind.Leave) => new()
    {
        Id = Guid.NewGuid(),
        AppointmentId = Guid.NewGuid(),
        PlanId = Guid.NewGuid(),
        PlanVersion = 1,
        Kind = kind,
        Text = "Time to leave at 09:37 by bike.",
        ReplacesNudgeId = replaces,
    };

    [Fact]
    public async Task Delivers_to_the_subscription_and_reports_success()
    {
        AddSubscription();
        var stub = new StubHttpHandler((HttpStatusCode.Created, ""));

        var sent = await Transport(stub).SendAsync(LeaveNudge());

        Assert.True(sent);
        Assert.Equal(1, stub.Calls);
    }

    [Fact]
    public async Task Payload_carries_kind_title_body_url_and_ids()
    {
        var nudge = LeaveNudge();
        var payload = JsonSerializer.Deserialize<JsonElement>(WebPushTransport.PayloadFor(nudge));

        Assert.Equal("Leave", payload.GetProperty("kind").GetString());
        Assert.Equal("Time to leave", payload.GetProperty("title").GetString());
        Assert.Equal(nudge.Text, payload.GetProperty("body").GetString());
        Assert.Equal("/now", payload.GetProperty("url").GetString());
        Assert.Equal(nudge.Id, payload.GetProperty("nudgeId").GetGuid());
        Assert.Equal(nudge.PlanId, payload.GetProperty("tag").GetGuid()); // OS replaces per plan
    }

    [Fact]
    public async Task A_replacement_presents_itself_as_an_update()
    {
        var payload = JsonSerializer.Deserialize<JsonElement>(
            WebPushTransport.PayloadFor(LeaveNudge(replaces: Guid.NewGuid())));

        Assert.Equal("TEMPO — updated plan", payload.GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_habit_proposal_deep_links_to_habits_with_its_habit_id()
    {
        var habitId = Guid.NewGuid();
        var nudge = LeaveNudge(kind: NudgeKind.HabitProposal);
        nudge.HabitId = habitId;
        var payload = JsonSerializer.Deserialize<JsonElement>(WebPushTransport.PayloadFor(nudge));

        Assert.Equal("/habits", payload.GetProperty("url").GetString());
        Assert.Equal(habitId, payload.GetProperty("habitId").GetGuid());
    }

    [Fact]
    public async Task A_410_deletes_the_subscription_without_retry_and_others_still_deliver()
    {
        var gone = AddSubscription("https://push.example.org/gone");
        AddSubscription("https://push.example.org/alive");
        var stub = new StubHttpHandler((HttpStatusCode.Gone, ""), (HttpStatusCode.Created, ""));

        var sent = await Transport(stub).SendAsync(LeaveNudge());

        Assert.True(sent); // the second subscription got it
        Assert.Equal(2, stub.Calls); // no retry against the dead endpoint
        Assert.DoesNotContain(db.Context.PushSubscriptions, s => s.Id == gone.Id);
        Assert.Single(db.Context.PushSubscriptions);
    }

    [Fact]
    public async Task Transient_errors_are_retried_up_to_three_attempts()
    {
        AddSubscription();
        var stub = new StubHttpHandler(
            (HttpStatusCode.ServiceUnavailable, ""), (HttpStatusCode.ServiceUnavailable, ""), (HttpStatusCode.Created, ""));

        var sent = await Transport(stub).SendAsync(LeaveNudge());

        Assert.True(sent);
        Assert.Equal(3, stub.Calls);
    }

    [Fact]
    public async Task Exhausted_retries_report_failure()
    {
        AddSubscription();
        var stub = new StubHttpHandler((HttpStatusCode.ServiceUnavailable, ""));

        var sent = await Transport(stub).SendAsync(LeaveNudge());

        Assert.False(sent);
        Assert.Equal(3, stub.Calls);
        Assert.Single(db.Context.PushSubscriptions); // transient failure never deletes
    }

    [Fact]
    public async Task No_subscriptions_means_not_delivered()
    {
        var stub = new StubHttpHandler((HttpStatusCode.Created, ""));

        Assert.False(await Transport(stub).SendAsync(LeaveNudge()));
        Assert.Equal(0, stub.Calls);
    }

    public void Dispose() => db.Dispose();
}
