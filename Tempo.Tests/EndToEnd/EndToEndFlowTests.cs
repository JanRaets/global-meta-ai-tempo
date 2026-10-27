using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Tempo.Agent;
using Tempo.Core.Configuration;
using Tempo.Core.Domain;
using Tempo.Core.Location;
using Tempo.Core.Models;
using Tempo.Core.Geo;
using Tempo.Core.Nudging;
using Tempo.Core.Planning;
using Tempo.Integrations.Meta;
using Tempo.Jobs;
using Tempo.Tests.Fakes;
using Tempo.Tests.Nudging;

namespace Tempo.Tests.EndToEnd;

/// <summary>
/// Task 13.3 — the whole demo loop in one script: seed visits → inference → proposal →
/// confirm → plan includes the stop → voice "skip the coffee" → plan drops it → capture a
/// photo → new appointment → lateness → recovery nudge with a trace. The scripted variant
/// runs everywhere (fakes for model/routing/location); the live variant replays the same
/// flow against the real Meta API when a key is present (routing stays fake — ORS quota).
/// </summary>
public sealed class EndToEndFlowTests : IDisposable
{
    // Tuesday morning, 08:00 local (07:00Z): visits seeded on past Tuesdays, meeting today 09:30.
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-11-03T07:00:00Z"));
    private readonly TestDb db = new();
    private readonly FakeLocationBuffer buffer = new();
    private readonly FakeRoutingClient routing = new();
    private readonly RecordingNudgeTransport transport = new();
    private readonly CollectingDomainEvents events = new();

    [Fact]
    public async Task The_full_loop_with_the_scripted_model()
    {
        var model = new ScriptedMetaModels();
        await RunFullLoopAsync(model, scripted: model);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task The_full_loop_with_the_live_meta_api()
    {
        var options = LoadOptions();
        Skip.If(string.IsNullOrEmpty(options.MetaApi.ApiKey), "no API key (Models:MetaApi:ApiKey)");
        using var http = new HttpClient
        {
            BaseAddress = new Uri(options.MetaApi.BaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(120),
        };
        http.DefaultRequestHeaders.Authorization = new("Bearer", options.MetaApi.ApiKey);
        var models = new MetaApiModels(http, Options.Create(options), new InMemoryModelCallLog(),
            NullLogger<MetaApiModels>.Instance);
        await RunFullLoopAsync(models, scripted: null);
    }

    private async Task RunFullLoopAsync(IMetaModels models, ScriptedMetaModels? scripted)
    {
        // ---- stage 0: seed — home, café, office; three past Tuesdays of home→café→meeting ----
        var settings = new UserSettings { Id = Guid.NewGuid(), LocationApiKey = "k", EnabledModes = [TravelMode.Bicycle] };
        var home = new Place { Id = Guid.NewGuid(), Name = "Thuis", Lat = 52.0950, Lng = 5.2400 };
        var cafe = new Place { Id = Guid.NewGuid(), Name = "Bar Bello", Lat = 52.0905, Lng = 5.2000 };
        var office = new Place { Id = Guid.NewGuid(), Name = "Utrecht Centraal", Lat = 52.0894, Lng = 5.1100 };
        settings.HomePlaceId = home.Id;
        db.Context.AddRange(settings, home, cafe, office);
        var tz = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone);
        DateTimeOffset Past(int weeksBack, int hour, int minute)
        {
            var local = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), tz).Date.AddDays(-7 * weeksBack).AddHours(hour).AddMinutes(minute);
            return new DateTimeOffset(local, tz.GetUtcOffset(local)).ToUniversalTime();
        }
        for (var week = 1; week <= 3; week++)
        {
            db.Context.Visits.Add(new Visit { Id = Guid.NewGuid(), PlaceId = home.Id, ArrivedAt = Past(week, 7, 30), DepartedAt = Past(week, 8, 40) });
            db.Context.Visits.Add(new Visit { Id = Guid.NewGuid(), PlaceId = cafe.Id, ArrivedAt = Past(week, 8, 50), DepartedAt = Past(week, 8, 59) });
            db.Context.Appointments.Add(new Appointment
            {
                Id = Guid.NewGuid(),
                Source = AppointmentSource.Captured,
                Title = "Team meeting",
                Start = Past(week, 9, 30),
                End = Past(week, 10, 30),
                PlaceId = office.Id,
            });
        }
        var meeting = new Appointment
        {
            Id = Guid.NewGuid(),
            Source = AppointmentSource.Captured,
            Title = "Team meeting",
            Start = Past(0, 9, 30),
            End = Past(0, 10, 30),
            PlaceId = office.Id,
            Place = office,
        };
        db.Context.Appointments.Add(meeting);
        db.Context.SaveChanges();
        buffer.Points.Add(new LocationPoint(home.Lat, home.Lng, clock.GetUtcNow().AddMinutes(-2))); // at home

        // ---- stage 1: inference turns the visits into a proposed habit ----
        scripted?.EnqueueTyped(new HabitProposalText("Coffee at Bar Bello before your Tuesday meeting — plan for it?", "3 visits, ~9 min each"));
        var wording = new HabitWording(models, NullLogger<HabitWording>.Instance);
        await new HabitInferenceService(db.Context, wording, clock, NullLogger<HabitInferenceService>.Instance).RunAsync();
        var habit = Assert.Single(db.Context.Habits.Where(h => h.State == HabitState.Proposed));

        // ---- stage 2: the daily batch proposes it in one message ----
        await new HabitProposalBatchService(db.Context, transport, clock, NullLogger<HabitProposalBatchService>.Instance).RunAsync();
        Assert.Equal(NudgeKind.HabitProposal, Assert.Single(transport.Delivered).Kind);

        // ---- stage 3: confirm through the one handler ----
        await new ConfirmHabitHandler(db.Context, events).ConfirmAsync(habit.Id);
        Assert.Single(events.Published.OfType<Tempo.Core.Events.HabitConfirmed>());

        // ---- stage 4: the agent plans; the committed plan includes the café ----
        var store = new PlanOptionStore();
        var plans = new PlanService(db.Context, new DeparturePlanner(routing, NullLogger<DeparturePlanner>.Instance),
            store, new CurrentLocationProvider(buffer, clock), clock, NullLogger<PlanService>.Instance);
        var options = await plans.OptionsAsync(meeting.Id);
        var withStop = options.Options.First(o => o.Stops.Any(s => s.PlaceId == cafe.Id));
        var commit = await plans.CommitAsync(withStop.OptionId, "e2e: includes the habitual stop", PlanCommitter.Agent);
        Assert.Contains(commit.Plan!.Stops, s => s.PlaceId == cafe.Id);

        // ---- stage 5: voice "skip the coffee" drops the stop on re-plan ----
        scripted?.EnqueueTyped(new IntentClassification { Type = "SkipStop", StopName = "Bar Bello" });
        var voice = new VoiceIntentService(db.Context, models, plans, new ConfirmHabitHandler(db.Context, events),
            events, clock, NullLogger<VoiceIntentService>.Instance);
        var voiceResult = await voice.HandleTextAsync("skip the coffee today");
        Assert.True(voiceResult.Applied);
        var replanned = await plans.OptionsAsync(meeting.Id);
        Assert.DoesNotContain(replanned.Options, o => o.Stops.Any(s => s.PlaceId == cafe.Id));
        var recommit = await plans.CommitAsync(replanned.Options.First(o => o.IsFeasible).OptionId, "e2e: without the stop", PlanCommitter.Agent);
        Assert.Empty(recommit.Plan!.Stops);

        // ---- stage 6: capture a photo → a new appointment exists ----
        scripted?.EnqueueTyped(new CapturedAppointment
        {
            Title = "Dinner",
            DateText = "Thursday",
            TimeText = "19:00",
            PlaceText = "Bar Bello",
            Confidence = 0.95,
            RawText = "Dinner Thursday 19:00 at Bar Bello",
        });
        var geocoder = new FakeGeocoder().Knows("Bar Bello", cafe.Lat, cafe.Lng);
        var captureService = new CaptureService(db.Context, models, new PlaceResolver(db.Context, geocoder),
            geocoder, events, clock, NullLogger<CaptureService>.Instance);
        var captureBytes = scripted is null
            ? await File.ReadAllBytesAsync(Path.Combine(TestPaths.RepoRoot(), "Tempo.Tests", "Capture", "Images", "whatsapp.png"))
            : new byte[100];
        var capture = await captureService.CaptureAsync(captureBytes, "image/png");
        Assert.True(capture.Found);
        Assert.NotNull(capture.StartLocal);
        var confirmed = await captureService.ConfirmAsync(new ConfirmCapture(capture.Title, capture.StartLocal!, capture.PlaceText));
        Assert.Contains(db.Context.Appointments, a => a.Id == confirmed.AppointmentId && a.Source == AppointmentSource.Captured);

        // ---- stage 7: lateness → the tick guarantees a Late recovery nudge with a trace ----
        clock.SetUtcNow(recommit.Plan!.LeaveAt.AddMinutes(8)); // past the current plan's leaveAt, before the meeting
        buffer.Points.Add(new LocationPoint(home.Lat, home.Lng, clock.GetUtcNow().AddMinutes(-1))); // still at home
        scripted?.EnqueueFinal("Late run that fumbles the nudge."); // the template then takes over
        var runner = BuildRunner(models);
        var nudges = new NudgeService(db.Context, store, transport, clock, NullLogger<NudgeService>.Instance);
        var tick = new PlannerTickService(db.Context, runner, plans, nudges, new TickStateStore(),
            new CurrentLocationProvider(buffer, clock), clock, NullLogger<PlannerTickService>.Instance);
        await tick.TickAsync();

        var late = Assert.Single(transport.Delivered.Where(n => n.Kind == NudgeKind.Late));
        Assert.NotNull(late.AgentTraceId); // "Why?" has a trace to show
        Assert.NotEmpty(db.Context.AgentTraces.ToList());
    }

    private AgentRunner BuildRunner(IMetaModels models)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(db.Context);
        services.AddSingleton(models);
        services.AddSingleton<ILocationBuffer>(buffer);
        services.AddSingleton(new CurrentLocationProvider(buffer, clock));
        services.AddSingleton<IRoutingClient>(routing);
        services.AddScoped<DeparturePlanner>();
        services.AddScoped<PlanOptionStore>();
        services.AddScoped<PlanService>();
        services.AddSingleton<INudgeTransport>(transport);
        services.AddScoped<NudgeService>();
        var provider = services.BuildServiceProvider();
        return new AgentRunner(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AgentRunner>.Instance);
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

    public void Dispose() => db.Dispose();
}
