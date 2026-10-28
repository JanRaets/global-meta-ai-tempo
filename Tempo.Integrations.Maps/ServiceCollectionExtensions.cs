using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Tempo.Core.Configuration;
using Tempo.Core.Geo;

namespace Tempo.Integrations.Maps;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMapsIntegration(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RoutingOptions>().Bind(configuration.GetSection(RoutingOptions.Section));
        // Routing:ApiKey is checked on first use, not at startup: the API must boot before
        // the ORS key exists (unlike Models, nothing works without which).
        services.AddHttpClient<OrsGeocoder>((sp, http) =>
        {
            var o = sp.GetRequiredService<IOptions<RoutingOptions>>().Value;
            http.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");
            if (o.ApiKey is not "")
                http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", o.ApiKey); // raw key; typed parser rejects it
            http.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddScoped<IGeocoder>(sp => sp.GetRequiredService<OrsGeocoder>());
        services.AddHttpClient<OrsRoutingClient>((sp, http) =>
        {
            var o = sp.GetRequiredService<IOptions<RoutingOptions>>().Value;
            http.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");
            if (o.ApiKey is not "")
                http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", o.ApiKey); // ORS: raw key, no Bearer
            http.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddHttpClient<TomTomRoutingClient>((sp, http) =>
        {
            var o = sp.GetRequiredService<IOptions<RoutingOptions>>().Value.TomTom;
            http.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");
            http.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddSingleton<TomTomDailyCounter>();
        // Car → TomTom (traffic-aware; ORS on failure), bike/walk → ORS (added 28 Oct 2026).
        services.AddScoped<IRoutingClient, CompositeRoutingClient>();
        return services;
    }
}
