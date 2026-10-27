using FastEndpoints;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Tempo.Core.Configuration;
using Tempo.Core.Data;
using Tempo.Core.Domain;
using Tempo.Core.Events;
using Tempo.Core.Geo;
using Tempo.Core.Location;
using Tempo.Core.Redis;
using Tempo.Integrations.Google;
using Tempo.Integrations.Maps;
using Tempo.Integrations.Meta;
using Tempo.Integrations.Push;
using Tempo.Jobs;

var builder = WebApplication.CreateBuilder(args);

// Gitignored local overrides (API keys on a dev machine; see .gitignore). Highest JSON precedence.
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);

// Config schema (fail fast; see architecture.md). Each integration enforces its own
// required values the moment it is registered.
builder.Services.AddMetaModels(builder.Configuration);
// Routing and Google options are bound by AddMapsIntegration / AddGoogleCalendar below.

var postgres = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Missing configuration value ConnectionStrings:Postgres");
builder.Services.AddDbContext<TempoDbContext>(o => o.UseNpgsql(postgres, b => b.MigrationsAssembly("Tempo.Api")));

builder.Services.AddSingleton<IDomainEvents, Tempo.Api.HangfireDomainEvents>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ConfirmHabitHandler>();
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RoutingOptions>>().Value);
builder.Services.AddScoped<Tempo.Core.Planning.DeparturePlanner>();
builder.Services.AddScoped<Tempo.Core.Planning.PlanOptionStore>();
builder.Services.AddScoped<Tempo.Core.Planning.PlanService>();
builder.Services.AddScoped<Tempo.Core.Nudging.NudgeService>();
builder.Services.AddWebPush(builder.Configuration); // INudgeTransport (design D16); logs instead when VAPID keys are missing
builder.Services.AddOptions<AgentOptions>().Bind(builder.Configuration.GetSection(AgentOptions.Section));
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value);
builder.Services.AddOptions<PhaseOptions>().Bind(builder.Configuration.GetSection(PhaseOptions.Section));
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PhaseOptions>>().Value);
builder.Services.AddScoped<Tempo.Core.Planning.PlanPhaseService>();
builder.Services.AddSingleton<Tempo.Agent.AgentRunner>();
builder.Services.AddSingleton<Tempo.Agent.TickStateStore>();
builder.Services.AddScoped<Tempo.Agent.VoiceIntentService>();
builder.Services.AddScoped<Tempo.Agent.CaptureService>();
builder.Services.AddScoped<Tempo.Agent.PlannerTickService>();
builder.Services.AddScoped<Tempo.Agent.PlannerTickJob>();
builder.Services.AddScoped<Tempo.Agent.AgentRunJob>();
builder.Services.AddScoped<PlaceResolver>();
builder.Services.AddScoped<CalendarSyncService>();
builder.Services.AddScoped<CalendarSyncJob>();

var redisConnection = builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("Missing configuration value ConnectionStrings:Redis");
builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(
    StackExchange.Redis.ConnectionMultiplexer.Connect(redisConnection + ",abortConnect=false"));
builder.Services.AddSingleton<RoutingCacheStats>();
builder.Services.AddSingleton<IRoutingCache, RedisRoutingCache>();
builder.Services.AddSingleton<ILocationBuffer, RedisLocationBuffer>();
builder.Services.AddSingleton<LocationIngestService>();
builder.Services.AddSingleton<CurrentLocationProvider>();
builder.Services.AddScoped<Tempo.Api.Endpoints.LocationApiKeyAuth>();
builder.Services.AddScoped<CollapseVisitsService>();
builder.Services.AddScoped<CollapseVisitsJob>();
builder.Services.AddScoped<HabitWording>();
builder.Services.AddScoped<HabitInferenceService>();
builder.Services.AddScoped<HabitInferenceJob>();
builder.Services.AddScoped<HabitProposalBatchService>();
builder.Services.AddScoped<HabitProposalBatchJob>();
builder.Services.AddScoped<DemoModeService>();
builder.Services.AddScoped<DemoModeJob>();

builder.Services.AddDataProtection();
builder.Services.AddMemoryCache();
builder.Services.AddGoogleCalendar(builder.Configuration);
builder.Services.AddMapsIntegration(builder.Configuration);

builder.Services.AddHangfire(c => c
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(o => o.UseNpgsqlConnection(postgres)));
builder.Services.AddHangfireServer();

builder.Services.AddFastEndpoints();

var app = builder.Build();

// Apply EF migrations on startup; retry while compose's Postgres comes up.
await using (var scope = app.Services.CreateAsyncScope())
{
    // Force options validation before touching the database, so a missing key
    // fails startup with the configuration error, not a DB error.
    _ = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ModelsOptions>>().Value;

    var push = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<PushOptions>>().Value;
    if (string.IsNullOrEmpty(push.VapidPublicKey) || string.IsNullOrEmpty(push.VapidPrivateKey))
        app.Logger.LogWarning("Push: VAPID keys missing — nudges go to the log only (set Push:VapidPublicKey/VapidPrivateKey in user-secrets)");

    var db = scope.ServiceProvider.GetRequiredService<TempoDbContext>();
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await db.Database.MigrateAsync();
            break;
        }
        catch (Exception ex) when (attempt < 15)
        {
            app.Logger.LogWarning("Postgres not ready (attempt {Attempt}): {Message}", attempt, ex.Message);
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    // Single-user app: make sure the settings row exists (location API key generated once).
    if (!await db.UserSettings.AnyAsync())
    {
        db.UserSettings.Add(new UserSettings
        {
            Id = Guid.NewGuid(),
            LocationApiKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)),
        });
        await db.SaveChangesAsync();
    }
}

app.Services.GetRequiredService<IRecurringJobManager>()
    .AddOrUpdate<CalendarSyncJob>("calendar-sync", j => j.RunAsync(CancellationToken.None), "*/5 * * * *");
app.Services.GetRequiredService<IRecurringJobManager>()
    .AddOrUpdate<CollapseVisitsJob>("collapse-visits", j => j.RunAsync(CancellationToken.None), "*/10 * * * *");
app.Services.GetRequiredService<IRecurringJobManager>()
    .AddOrUpdate<Tempo.Agent.PlannerTickJob>("planner-tick", j => j.RunAsync(CancellationToken.None), "*/2 * * * *");
app.Services.GetRequiredService<IRecurringJobManager>()
    .AddOrUpdate<HabitInferenceJob>("habit-inference", j => j.RunAsync(CancellationToken.None), "0 2 * * *");   // 03:00 local
app.Services.GetRequiredService<IRecurringJobManager>()
    .AddOrUpdate<HabitProposalBatchJob>("habit-proposal-batch", j => j.RunAsync(CancellationToken.None), "0 7 * * *"); // 08:00 local, after quiet hours

app.UseDefaultFiles();   // Tempo.Web builds into wwwroot
app.UseStaticFiles();
app.UseFastEndpoints();
app.MapFallbackToFile("index.html"); // SPA routes

app.Run();
