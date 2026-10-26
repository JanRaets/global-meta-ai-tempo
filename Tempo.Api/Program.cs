using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Tempo.Core.Configuration;
using Tempo.Core.Data;

var builder = WebApplication.CreateBuilder(args);

// Config schema (fail fast; see architecture.md). Each integration enforces its own
// required values the moment it is registered; Models is enforced here because the
// model layer is wired from section 1 on.
builder.Services.AddOptions<ModelsOptions>()
    .Bind(builder.Configuration.GetSection(ModelsOptions.Section))
    .Validate(o => o.Primary is not ModelProvider.MetaApi || o.MetaApi.ApiKey is not "",
        "Missing configuration value Models:MetaApi:ApiKey (primary provider is MetaApi)")
    .Validate(o => o.Primary is not ModelProvider.Baseten || o.Baseten.ApiKey is not "",
        "Missing configuration value Models:Baseten:ApiKey (primary provider is Baseten)")
    .ValidateOnStart();
builder.Services.AddOptions<RoutingOptions>().Bind(builder.Configuration.GetSection(RoutingOptions.Section));
builder.Services.AddOptions<TelegramOptions>().Bind(builder.Configuration.GetSection(TelegramOptions.Section));
builder.Services.AddOptions<GoogleOptions>().Bind(builder.Configuration.GetSection(GoogleOptions.Section));

var postgres = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Missing configuration value ConnectionStrings:Postgres");
builder.Services.AddDbContext<TempoDbContext>(o => o.UseNpgsql(postgres));

builder.Services.AddFastEndpoints();

var app = builder.Build();

// Apply EF migrations on startup; retry while compose's Postgres comes up.
await using (var scope = app.Services.CreateAsyncScope())
{
    // Force options validation before touching the database, so a missing key
    // fails startup with the configuration error, not a DB error.
    _ = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ModelsOptions>>().Value;

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
}

app.UseDefaultFiles();   // Tempo.Web builds into wwwroot
app.UseStaticFiles();
app.UseFastEndpoints();
app.MapFallbackToFile("index.html"); // SPA routes

app.Run();
