using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tempo.Core.Configuration;
using Tempo.Core.Models;
using Tempo.Integrations.Meta;
using Tempo.Tests.Fakes;

namespace Tempo.Tests.MetaModels;

internal static class TestClients
{
    public static readonly ModelsOptions Options = new()
    {
        Primary = ModelProvider.MetaApi,
        MetaApi = new MetaApiOptions { ApiKey = "test-key" },
        Baseten = new BasetenOptions { ApiKey = "test-key" },
    };

    public static MetaApiModels MetaApi(StubHttpHandler handler, IModelCallLog? log = null) =>
        new(Client(handler), Microsoft.Extensions.Options.Options.Create(Options),
            log ?? new InMemoryModelCallLog(), NullLogger<MetaApiModels>.Instance);

    public static BasetenModels Baseten(StubHttpHandler handler, MetaApiModels metaApi, IModelCallLog? log = null) =>
        new(Client(handler), Microsoft.Extensions.Options.Options.Create(Options), metaApi,
            log ?? new InMemoryModelCallLog(), NullLogger<BasetenModels>.Instance);

    public static StubHttpHandler ChatText(params string[] contents) =>
        new(contents.Select(c => (HttpStatusCode.OK, ChatBody(c))).ToArray());

    public static string ChatBody(string content) =>
        $$$"""{"choices":[{"message":{"content":{{{System.Text.Json.JsonSerializer.Serialize(content)}}}},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5}}""";

    private static HttpClient Client(StubHttpHandler handler) =>
        new(handler) { BaseAddress = new Uri("https://unit.test/v1/") };
}
