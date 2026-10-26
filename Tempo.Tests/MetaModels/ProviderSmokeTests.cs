using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tempo.Core.Configuration;
using Tempo.Core.Models;
using Tempo.Integrations.Meta;
using Xunit.Abstractions;

namespace Tempo.Tests.MetaModels;

/// <summary>
/// Task 1.8: one tool-call round trip against each real provider. The key is read from
/// Tempo.Api's user-secrets, Tempo.Api/appsettings(.local).json or environment variables;
/// without one the test reports as Skipped, never as Passed.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProviderSmokeTests(ITestOutputHelper output)
{
    private static readonly ToolDefinition EchoTool = new(
        "echo",
        "Echoes the given word back. Always call this tool.",
        """{"type":"object","properties":{"word":{"type":"string"}},"required":["word"]}""");

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

    [SkippableFact]
    public async Task MetaApi_tool_call_round_trip()
    {
        var options = LoadOptions();
        Skip.If(string.IsNullOrEmpty(options.MetaApi.ApiKey), "no API key (Models:MetaApi:ApiKey)");
        await RunAsync(ModelProvider.MetaApi, options, options.MetaApi.BaseUrl, options.MetaApi.ApiKey);
    }

    [SkippableFact]
    public async Task Baseten_tool_call_round_trip()
    {
        var options = LoadOptions();
        Skip.If(string.IsNullOrEmpty(options.Baseten.ApiKey), "no API key (Models:Baseten:ApiKey)");
        await RunAsync(ModelProvider.Baseten, options, options.Baseten.BaseUrl, options.Baseten.ApiKey);
    }

    private async Task RunAsync(ModelProvider provider, ModelsOptions options, string baseUrl, string apiKey)
    {
        var recorder = new RecordingHandler(new HttpClientHandler());
        using var http = new HttpClient(recorder)
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(60),
        };
        http.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
        var log = new InMemoryModelCallLog();
        var metaApi = new MetaApiModels(http, Options.Create(options), log, NullLogger<MetaApiModels>.Instance);
        IMetaModels models = provider == ModelProvider.MetaApi
            ? metaApi
            : new BasetenModels(http, Options.Create(options), metaApi, log, NullLogger<BasetenModels>.Instance);

        ChatResult? result = null;
        try
        {
            result = await models.ChatAsync(
                [ChatMessage.System("You are a test harness. Use the echo tool."), ChatMessage.User("Echo the word tempo.")],
                [EchoTool],
                new ChatCallOptions { Purpose = "integration-smoke" });
        }
        finally
        {
            foreach (var line in recorder.Log)
                output.WriteLine(line);
            if (result is not null)
            {
                output.WriteLine($"provider: {result.Provider}");
                output.WriteLine($"assistant text: {result.Text ?? "(none)"}");
                foreach (var call in result.ToolCalls)
                    output.WriteLine($"tool call: {call.Name}({call.ArgumentsJson})");
            }
        }

        Assert.Equal(provider, result!.Provider);
        var toolCall = Assert.Single(result.ToolCalls);
        Assert.Equal("echo", toolCall.Name);
        Assert.Contains("tempo", toolCall.ArgumentsJson, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Records method, URI and status of every HTTP exchange, plus error bodies.</summary>
    private sealed class RecordingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public List<string> Log { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            Log.Add($"HTTP {request.Method} {request.RequestUri} -> {(int)response.StatusCode} {response.StatusCode}");
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                Log.Add($"error body: {body[..Math.Min(body.Length, 500)]}");
            }
            return response;
        }
    }
}
