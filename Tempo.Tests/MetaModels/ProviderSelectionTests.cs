using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Tempo.Core.Configuration;
using Tempo.Core.Models;
using Tempo.Integrations.Meta;

namespace Tempo.Tests.MetaModels;

public sealed class ProviderSelectionTests
{
    private static ServiceProvider Build(params (string Key, string Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMetaModels(config);
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("MetaApi", ModelProvider.MetaApi, ModelProvider.Baseten)]
    [InlineData("Baseten", ModelProvider.Baseten, ModelProvider.MetaApi)]
    public void Primary_is_selected_from_configuration_and_other_is_fallback(
        string primary, ModelProvider expectedPrimary, ModelProvider expectedFallback)
    {
        using var sp = Build(("Models:Primary", primary),
            ("Models:MetaApi:ApiKey", "k1"), ("Models:Baseten:ApiKey", "k2"));

        var models = Assert.IsType<FallbackMetaModels>(sp.GetRequiredService<IMetaModels>());
        Assert.Equal(expectedPrimary, models.PrimaryProvider);
        Assert.Equal(expectedFallback, models.FallbackProvider);
    }

    [Fact]
    public void Missing_primary_key_fails_with_error_naming_the_key()
    {
        using var sp = Build(("Models:Primary", "MetaApi"), ("Models:Baseten:ApiKey", "k2"));

        var ex = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<IOptions<ModelsOptions>>().Value);
        Assert.Contains("Models:MetaApi:ApiKey", ex.Message);
    }

    [Fact]
    public void Missing_fallback_key_does_not_block_startup()
    {
        using var sp = Build(("Models:Primary", "MetaApi"), ("Models:MetaApi:ApiKey", "k1"));

        Assert.NotNull(sp.GetRequiredService<IOptions<ModelsOptions>>().Value);
    }
}
