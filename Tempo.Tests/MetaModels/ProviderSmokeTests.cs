using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tempo.Core.Configuration;
using Tempo.Core.Models;
using Tempo.Integrations.Meta;
using Xunit.Abstractions;

namespace Tempo.Tests.MetaModels;

/// <summary>
/// Task 1.8: one tool-call round trip against each real provider. Runs only when the
/// matching key is present in the environment; otherwise reports itself as skipped.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProviderSmokeTests(ITestOutputHelper output)
{
    private static readonly ToolDefinition EchoTool = new(
        "echo",
        "Echoes the given word back. Always call this tool.",
        """{"type":"object","properties":{"word":{"type":"string"}},"required":["word"]}""");

    [Fact]
    public Task MetaApi_tool_call_round_trip() => RunAsync(ModelProvider.MetaApi, "Models__MetaApi__ApiKey");

    [Fact]
    public Task Baseten_tool_call_round_trip() => RunAsync(ModelProvider.Baseten, "Models__Baseten__ApiKey");

    private async Task RunAsync(ModelProvider provider, string keyVariable)
    {
        var apiKey = Environment.GetEnvironmentVariable(keyVariable);
        if (string.IsNullOrEmpty(apiKey))
        {
            output.WriteLine($"SKIPPED: {keyVariable} not set");
            return;
        }

        var options = new ModelsOptions
        {
            MetaApi = new MetaApiOptions { ApiKey = provider == ModelProvider.MetaApi ? apiKey : "" },
            Baseten = new BasetenOptions { ApiKey = provider == ModelProvider.Baseten ? apiKey : "" },
        };
        var active = provider == ModelProvider.MetaApi ? options.MetaApi.BaseUrl : options.Baseten.BaseUrl;
        using var http = new HttpClient { BaseAddress = new Uri(active.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
        var log = new InMemoryModelCallLog();
        var metaApi = new MetaApiModels(http, Options.Create(options), log, NullLogger<MetaApiModels>.Instance);
        IMetaModels models = provider == ModelProvider.MetaApi
            ? metaApi
            : new BasetenModels(http, Options.Create(options), metaApi, log, NullLogger<BasetenModels>.Instance);

        var result = await models.ChatAsync(
            [ChatMessage.System("You are a test harness. Use the echo tool."), ChatMessage.User("Echo the word tempo.")],
            [EchoTool],
            new ChatCallOptions { Purpose = "integration-smoke" });

        output.WriteLine($"provider={result.Provider} text={result.Text} toolCalls={result.ToolCalls.Count}");
        Assert.Equal(provider, result.Provider);
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("echo", call.Name);
        Assert.Contains("tempo", call.ArgumentsJson, StringComparison.OrdinalIgnoreCase);
    }
}
