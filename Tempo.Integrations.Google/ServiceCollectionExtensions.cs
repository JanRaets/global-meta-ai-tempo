using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Tempo.Core.Calendar;
using Tempo.Core.Configuration;

namespace Tempo.Integrations.Google;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGoogleCalendar(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<GoogleOptions>().Bind(configuration.GetSection(GoogleOptions.Section));
        services.AddScoped<GoogleTokenStore>();
        services.AddHttpClient<GoogleOAuthService>(http => http.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient<GoogleCalendarClient>(http =>
        {
            http.BaseAddress = new Uri("https://www.googleapis.com/");
            http.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddScoped<ICalendarClient>(sp => sp.GetRequiredService<GoogleCalendarClient>());
        return services;
    }
}
