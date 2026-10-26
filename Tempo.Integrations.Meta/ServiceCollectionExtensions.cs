using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tempo.Core.Configuration;
using Tempo.Core.Models;

namespace Tempo.Integrations.Meta;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers IMetaModels: primary from Models:Primary, the other as automatic fallback.
    /// Startup fails naming the missing key when the primary has none (meta-models spec).</summary>
    public static IServiceCollection AddMetaModels(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ModelsOptions>()
            .Bind(configuration.GetSection(ModelsOptions.Section))
            .Validate(o => o.Primary is not ModelProvider.MetaApi || o.MetaApi.ApiKey is not "",
                "Missing configuration value Models:MetaApi:ApiKey (primary provider is MetaApi)")
            .Validate(o => o.Primary is not ModelProvider.Baseten || o.Baseten.ApiKey is not "",
                "Missing configuration value Models:Baseten:ApiKey (primary provider is Baseten)")
            .ValidateOnStart();

        services.AddSingleton<IModelCallLog, InMemoryModelCallLog>();

        services.AddHttpClient<MetaApiModels>((sp, http) =>
        {
            var o = sp.GetRequiredService<IOptions<ModelsOptions>>().Value.MetaApi;
            http.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");
            http.DefaultRequestHeaders.Authorization = new("Bearer", o.ApiKey);
            http.Timeout = TimeSpan.FromSeconds(60);
        });

        services.AddHttpClient<BasetenModels>((sp, http) =>
        {
            var o = sp.GetRequiredService<IOptions<ModelsOptions>>().Value.Baseten;
            http.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");
            http.DefaultRequestHeaders.Authorization = new("Bearer", o.ApiKey);
            http.Timeout = TimeSpan.FromSeconds(60);
        });

        services.AddTransient<IMetaModels>(sp =>
        {
            var primary = sp.GetRequiredService<IOptions<ModelsOptions>>().Value.Primary;
            var metaApi = sp.GetRequiredService<MetaApiModels>();
            var baseten = sp.GetRequiredService<BasetenModels>();
            var logger = sp.GetRequiredService<ILogger<FallbackMetaModels>>();
            return primary == ModelProvider.MetaApi
                ? new FallbackMetaModels(metaApi, baseten, ModelProvider.MetaApi, ModelProvider.Baseten, logger)
                : new FallbackMetaModels(baseten, metaApi, ModelProvider.Baseten, ModelProvider.MetaApi, logger);
        });

        return services;
    }
}
