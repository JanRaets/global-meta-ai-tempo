using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Tempo.Core.Configuration;
using Tempo.Core.Models;
using Tempo.Integrations.Meta;
using Tempo.Tests.Fakes;

namespace Tempo.Tests.MetaModels;

public sealed class FallbackTests
{
    private static readonly IReadOnlyList<ChatMessage> Messages = [ChatMessage.User("hi")];

    private static FallbackMetaModels Wrap(IMetaModels primary, IMetaModels fallback) =>
        new(primary, fallback, ModelProvider.MetaApi, ModelProvider.Baseten, NullLogger<FallbackMetaModels>.Instance);

    [Fact]
    public async Task Rate_limited_primary_falls_back_and_result_records_fallback_provider()
    {
        var metaApi = TestClients.MetaApi(new StubHttpHandler((HttpStatusCode.TooManyRequests, """{"error":"rate limit"}""")));
        var baseten = TestClients.Baseten(TestClients.ChatText("hello from fallback"), metaApi);

        var result = await Wrap(metaApi, baseten).ChatAsync(Messages, []);

        Assert.Equal(ModelProvider.Baseten, result.Provider);
        Assert.Equal("hello from fallback", result.Text);
    }

    [Fact]
    public async Task Server_error_primary_falls_back()
    {
        var metaApi = TestClients.MetaApi(new StubHttpHandler((HttpStatusCode.BadGateway, "oops")));
        var baseten = TestClients.Baseten(TestClients.ChatText("ok"), metaApi);

        var result = await Wrap(metaApi, baseten).ChatAsync(Messages, []);

        Assert.Equal(ModelProvider.Baseten, result.Provider);
    }

    [Fact]
    public async Task Both_providers_failing_throws_ModelUnavailable()
    {
        var metaApi = TestClients.MetaApi(new StubHttpHandler((HttpStatusCode.TooManyRequests, "limit")));
        var baseten = TestClients.Baseten(new StubHttpHandler((HttpStatusCode.ServiceUnavailable, "down")), metaApi);

        await Assert.ThrowsAsync<ModelUnavailableException>(() => Wrap(metaApi, baseten).ChatAsync(Messages, []));
    }

    [Fact]
    public async Task Client_error_is_not_retried_on_the_fallback()
    {
        var primaryHandler = new StubHttpHandler((HttpStatusCode.BadRequest, """{"error":"bad schema"}"""));
        var metaApi = TestClients.MetaApi(primaryHandler);
        var baseten = TestClients.Baseten(TestClients.ChatText("should never be used"), metaApi);

        var ex = await Assert.ThrowsAsync<ModelHttpException>(() => Wrap(metaApi, baseten).ChatAsync(Messages, []));
        Assert.Equal(400, ex.StatusCode);
        Assert.Equal(1, primaryHandler.Calls);
    }
}
