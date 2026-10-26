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
                http.DefaultRequestHeaders.Add("Authorization", o.ApiKey);
            http.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddScoped<IGeocoder>(sp => sp.GetRequiredService<OrsGeocoder>());
        return services;
    }
}
