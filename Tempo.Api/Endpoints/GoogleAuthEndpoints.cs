using FastEndpoints;
using Hangfire;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Tempo.Core.Configuration;
using Tempo.Integrations.Google;
using Tempo.Jobs;

namespace Tempo.Api.Endpoints;

/// <summary>Starts the Google consent flow (readonly calendar scope only).</summary>
public sealed class GoogleConnectEndpoint(IOptions<GoogleOptions> options, IMemoryCache cache) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/auth/google/connect");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (options.Value.ClientId is "" || options.Value.ClientSecret is "")
        {
            AddError("Missing configuration value Google:ClientId / Google:ClientSecret");
            await Send.ErrorsAsync(503, ct);
            return;
        }
        var state = Guid.NewGuid().ToString("N");
        cache.Set($"google-oauth-state:{state}", true, TimeSpan.FromMinutes(10));
        await Send.RedirectAsync(GoogleOAuthService.BuildConsentUrl(options.Value, state), allowRemoteRedirects: true);
    }
}

public sealed class GoogleCallbackRequest
{
    [QueryParam] public string? Code { get; set; }
    [QueryParam] public string? State { get; set; }
    [QueryParam] public string? Error { get; set; }
}

/// <summary>Exchanges the consent code, stores the refresh token encrypted, enqueues an initial sync.</summary>
public sealed class GoogleCallbackEndpoint(GoogleOAuthService oauth, IMemoryCache cache, IBackgroundJobClient jobs)
    : Endpoint<GoogleCallbackRequest>
{
    public override void Configure()
    {
        Get("/auth/google/callback");
        AllowAnonymous();
    }

    public override async Task HandleAsync(GoogleCallbackRequest req, CancellationToken ct)
    {
        if (req.Error is not null || req.Code is null)
        {
            AddError($"Google consent failed: {req.Error ?? "no code"}");
            await Send.ErrorsAsync(400, ct);
            return;
        }
        if (req.State is null || !cache.TryGetValue($"google-oauth-state:{req.State}", out _))
        {
            AddError("Invalid or expired OAuth state");
            await Send.ErrorsAsync(400, ct);
            return;
        }
        cache.Remove($"google-oauth-state:{req.State}");
        await oauth.ExchangeCodeAsync(req.Code, ct);
        jobs.Enqueue<CalendarSyncJob>(j => j.RunAsync(CancellationToken.None));
        await Send.RedirectAsync("/?connected=google");
    }
}

/// <summary>On-demand calendar sync (calendar-sync spec: every 5 minutes and on demand).</summary>
public sealed class CalendarSyncNowEndpoint(IBackgroundJobClient jobs) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/sync/calendar");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        jobs.Enqueue<CalendarSyncJob>(j => j.RunAsync(CancellationToken.None));
        await Send.OkAsync(new { enqueued = true }, ct);
    }
}
