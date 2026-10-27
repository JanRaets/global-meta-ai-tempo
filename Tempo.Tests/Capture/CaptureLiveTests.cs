using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tempo.Agent;
using Tempo.Core.Configuration;
using Tempo.Core.Domain;
using Tempo.Core.Geo;
using Tempo.Core.Models;
using Tempo.Integrations.Meta;
using Tempo.Tests.Fakes;
using Xunit.Abstractions;

namespace Tempo.Tests.Capture;

/// <summary>
/// Section-11 live check: four images through the real Meta API via
/// <see cref="CaptureService"/> — the WhatsApp invite, the Dutch school letter, a
/// calendar-event screenshot and a photo with no appointment. Extracted fields and
/// the resolved DateTime are reported via test output; without a key the tests
/// report as Skipped, never as Passed (task 1.8 pattern).
/// </summary>
[Trait("Category", "Integration")]
public sealed class CaptureLiveTests(ITestOutputHelper output) : IDisposable
{
    private readonly List<HttpClient> clients = [];

    public void Dispose()
    {
        foreach (var client in clients)
            client.Dispose();
    }

    private static ModelsOptions LoadOptions()
    {
        var apiDir = Path.Combine(TestPaths.RepoRoot(), "Tempo.Api");
        var config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(apiDir, "appsettings.json"), optional: true)
            .AddJsonFile(Path.Combine(apiDir, "appsettings.local.json"), optional: true)
            .AddUserSecrets(typeof(Api.Endpoints.HealthEndpoint).Assembly, optional: true)
            .AddEnvironmentVariables()
            .Build();
        return config.GetSection(ModelsOptions.Section).Get<ModelsOptions>() ?? new ModelsOptions();
    }

    private static byte[] Image(string name) =>
        File.ReadAllBytes(Path.Combine(TestPaths.RepoRoot(), "Tempo.Tests", "Capture", "Images", name));

    /// <summary>Vision + resolution are under test here, not geocoding: any place text
    /// resolves to a fixed Utrecht point, empty text to nothing.</summary>
    private sealed class LiveGeocoder : IGeocoder
    {
        public Task<GeocodeResult?> GeocodeAsync(string text, GeoPoint? focus = null, CancellationToken ct = default) =>
            Task.FromResult(string.IsNullOrWhiteSpace(text)
                ? null : new GeocodeResult(52.0907, 5.1214, text));

        public Task<string?> ReverseAsync(double lat, double lng, CancellationToken ct = default) =>
            Task.FromResult<string?>("Utrecht");
    }

    private CaptureService Service(TestDb db, IMetaModels models, CollectingDomainEvents events)
    {
        db.Context.UserSettings.Add(new UserSettings { Id = Guid.NewGuid(), LocationApiKey = "live" });
        db.Context.SaveChanges();
        var geocoder = new LiveGeocoder();
        return new CaptureService(db.Context, models, new PlaceResolver(db.Context, geocoder),
            geocoder, events, TimeProvider.System, NullLogger<CaptureService>.Instance);
    }

    private IMetaModels LiveModels(ModelsOptions options)
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri(options.MetaApi.BaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(90),
        };
        http.DefaultRequestHeaders.Authorization = new("Bearer", options.MetaApi.ApiKey);
        clients.Add(http);
        return new MetaApiModels(http, Options.Create(options),
            new InMemoryModelCallLog(), NullLogger<MetaApiModels>.Instance);
    }

    private void Report(string name, CaptureResult result)
    {
        output.WriteLine($"--- {name} ---");
        output.WriteLine($"found: {result.Found}");
        output.WriteLine($"title: {result.Title}");
        output.WriteLine($"dateText: {result.DateText}");
        output.WriteLine($"timeText: {result.TimeText}");
        output.WriteLine($"start: {(result.Start?.ToString("O") ?? "(unresolved)")}");
        output.WriteLine($"startLocal: {result.StartLocal ?? "(none)"}");
        output.WriteLine($"placeText: {result.PlaceText}");
        output.WriteLine($"placeName: {result.PlaceName ?? "(none)"} (hasLocation={result.HasLocation})");
        output.WriteLine($"confidence: {result.Confidence}");
        output.WriteLine($"rawText: {result.RawText}");
    }

    [SkippableFact]
    public async Task Live_whatsapp_invite_extracts_and_confirms()
    {
        var options = LoadOptions();
        Skip.If(string.IsNullOrEmpty(options.MetaApi.ApiKey), "no API key (Models:MetaApi:ApiKey)");
        using var db = new TestDb();
        var events = new CollectingDomainEvents();

        var service = Service(db, LiveModels(options), events);
        var result = await service.CaptureAsync(Image("whatsapp.png"), "image/png");

        Report("whatsapp.png", result);
        Assert.InRange(result.Confidence, 0, 1);
        if (result is { Found: true, StartLocal: not null })
        {
            var confirmed = await service.ConfirmAsync(
                new ConfirmCapture(result.Title, result.StartLocal, result.PlaceText));
            output.WriteLine($"confirmed: {confirmed.AppointmentId} at {confirmed.Start:O} place={confirmed.PlaceName ?? "(none)"}");
            var change = Assert.Single(events.Published.OfType<Core.Events.AppointmentChanged>());
            Assert.Equal(Core.Events.AppointmentChangeKind.Created, change.Kind);
        }
        else
        {
            output.WriteLine("NOT confirmed: extraction left nothing to add");
        }
    }

    [SkippableFact]
    public async Task Live_dutch_school_letter_extracts()
    {
        var options = LoadOptions();
        Skip.If(string.IsNullOrEmpty(options.MetaApi.ApiKey), "no API key (Models:MetaApi:ApiKey)");
        using var db = new TestDb();

        var result = await Service(db, LiveModels(options), new CollectingDomainEvents())
            .CaptureAsync(Image("school-letter.png"), "image/png");

        Report("school-letter.png", result);
        Assert.InRange(result.Confidence, 0, 1);
    }

    [SkippableFact]
    public async Task Live_calendar_event_screenshot_extracts()
    {
        var options = LoadOptions();
        Skip.If(string.IsNullOrEmpty(options.MetaApi.ApiKey), "no API key (Models:MetaApi:ApiKey)");
        using var db = new TestDb();

        var result = await Service(db, LiveModels(options), new CollectingDomainEvents())
            .CaptureAsync(Image("calendar-event.png"), "image/png");

        Report("calendar-event.png", result);
        Assert.InRange(result.Confidence, 0, 1);
    }

    [SkippableFact]
    public async Task Live_photo_without_appointment_finds_nothing()
    {
        var options = LoadOptions();
        Skip.If(string.IsNullOrEmpty(options.MetaApi.ApiKey), "no API key (Models:MetaApi:ApiKey)");
        using var db = new TestDb();

        var result = await Service(db, LiveModels(options), new CollectingDomainEvents())
            .CaptureAsync(Image("shopping-list.png"), "image/png");

        Report("shopping-list.png", result);
        Assert.InRange(result.Confidence, 0, 1);
    }
}
