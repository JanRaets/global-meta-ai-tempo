using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tempo.Core.Configuration;
using Tempo.Core.Domain;
using Tempo.Core.Geo;
using Tempo.Integrations.Maps;
using Xunit.Abstractions;

namespace Tempo.Tests.Routing;

/// <summary>Task 4.x: one real Estimate and one real Detour against ORS. Key from
/// Tempo.Api user-secrets / appsettings(.local).json / env; skipped without one.</summary>
[Trait("Category", "Integration")]
public sealed class OrsSmokeTests(ITestOutputHelper output)
{
    private static readonly GeoPoint Zeist = new(52.0906, 5.2332);
    private static readonly GeoPoint UtrechtCentraal = new(52.0894, 5.1100);
    private static readonly GeoPoint DeBilt = new(52.1095, 5.1850);

    [SkippableFact]
    public async Task Real_estimate_and_detour_Zeist_to_Utrecht_by_bike()
    {
        var apiDir = Path.Combine(TestPaths.RepoRoot(), "Tempo.Api");
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(apiDir, "appsettings.json"), optional: true)
            .AddJsonFile(Path.Combine(apiDir, "appsettings.local.json"), optional: true)
            .AddUserSecrets(typeof(Api.Endpoints.HealthEndpoint).Assembly, optional: true)
            .AddEnvironmentVariables()
            .Build();
        var options = config.GetSection(RoutingOptions.Section).Get<RoutingOptions>() ?? new RoutingOptions();
        Skip.If(string.IsNullOrEmpty(options.ApiKey), "no API key (Routing:ApiKey)");

        using var http = new HttpClient { BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", options.ApiKey);
        var client = new OrsRoutingClient(http, Options.Create(options), new FakeRoutingCache(), NullLogger<OrsRoutingClient>.Instance);

        TimeSpan estimate, detour;
        try
        {
            estimate = await client.EstimateAsync(Zeist, UtrechtCentraal, TravelMode.Bicycle, DateTimeOffset.UtcNow);
            detour = await client.DetourAsync(Zeist, [DeBilt], UtrechtCentraal, TravelMode.Bicycle);
        }
        catch (RoutingUnavailableException ex) when (ex.Message.Contains("unreachable"))
        {
            // A live smoke skips when the service itself is down (observed 27 Oct: ORS
            // outage, even /health timing out) — it fails only on wrong behaviour.
            Skip.If(true, $"ORS unreachable: {ex.Message}");
            return;
        }

        output.WriteLine($"Estimate Zeist -> Utrecht Centraal (cycling-regular): {estimate.TotalMinutes:F1} min");
        output.WriteLine($"Detour Zeist -> De Bilt -> Utrecht Centraal (cycling-regular): {detour.TotalMinutes:F1} min");
        Assert.InRange(estimate.TotalMinutes, 15, 120);
        Assert.True(detour > estimate, "detour via De Bilt must take longer than the direct route");
    }
}
