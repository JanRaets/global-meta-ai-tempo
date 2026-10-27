using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tempo.Core.Configuration;
using Tempo.Core.Data;
using Tempo.Core.Nudging;

namespace Tempo.Integrations.Push;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers `INudgeTransport`. With VAPID keys configured it is the real Web Push
    /// transport (design D16); without them it falls back to the logging transport so a fresh
    /// clone still runs — the startup log says which one is active.</summary>
    public static IServiceCollection AddWebPush(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PushOptions>().Bind(configuration.GetSection(PushOptions.Section));
        services.AddHttpClient(nameof(WebPushTransport));
        services.AddScoped<INudgeTransport>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<PushOptions>>();
            var o = options.Value;
            if (string.IsNullOrEmpty(o.VapidPublicKey) || string.IsNullOrEmpty(o.VapidPrivateKey))
                return new LoggingNudgeTransport(sp.GetRequiredService<ILogger<LoggingNudgeTransport>>());
            var client = new PushServiceClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(WebPushTransport)))
            {
                DefaultAuthentication = new VapidAuthentication(o.VapidPublicKey, o.VapidPrivateKey) { Subject = o.Subject },
            };
            return new WebPushTransport(
                sp.GetRequiredService<TempoDbContext>(), client, options, sp.GetRequiredService<ILogger<WebPushTransport>>());
        });
        return services;
    }
}
