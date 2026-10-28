using System.Net;
using Tempo.Core.Models;
using Tempo.Tests.Fakes;

namespace Tempo.Tests.MetaModels;

/// <summary>13.b, observed live 28 Oct 2026: Baseten validates function names against
/// [a-zA-Z0-9_-] and 400s on our canonical dotted tool names. The registry, prompt and
/// traces keep the dots; BasetenModels swaps them for underscores on the wire only.</summary>
public sealed class ToolNameShapeTests
{
    private static readonly ToolDefinition Upcoming = new(
        "calendar.upcoming", "Upcoming appointments.", """{"type":"object","properties":{}}""");

    private const string ToolCallBody =
        """{"choices":[{"message":{"tool_calls":[{"id":"c1","type":"function","function":{"name":"calendar_upcoming","arguments":"{}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""";

    [Fact]
    public async Task Baseten_sends_underscores_and_returns_dots()
    {
        var handler = new StubHttpHandler((HttpStatusCode.OK, ToolCallBody));
        var models = TestClients.Baseten(handler, TestClients.MetaApi(new StubHttpHandler()));

        var result = await models.ChatAsync(
            [
                ChatMessage.User("plan"),
                // A prior assistant turn's tool call must also go out with the wire shape.
                ChatMessage.Assistant("", [new ToolCall("c0", "plan.options", "{}")]),
                ChatMessage.ToolResult("c0", "{}"),
            ],
            [Upcoming]);

        var body = Assert.Single(handler.RequestBodies);
        Assert.Contains("calendar_upcoming", body);
        Assert.Contains("plan_options", body);
        Assert.DoesNotContain("calendar.upcoming", body);
        Assert.DoesNotContain("plan.options", body);
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("calendar.upcoming", call.Name); // back to canonical for the loop and traces
    }

    [Fact]
    public async Task MetaApi_keeps_dotted_names()
    {
        var metaBody =
            """{"choices":[{"message":{"tool_calls":[{"id":"c1","type":"function","function":{"name":"calendar.upcoming","arguments":"{}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""";
        var handler = new StubHttpHandler((HttpStatusCode.OK, metaBody));
        var models = TestClients.MetaApi(handler);

        var result = await models.ChatAsync([ChatMessage.User("plan")], [Upcoming]);

        Assert.Contains("calendar.upcoming", Assert.Single(handler.RequestBodies));
        Assert.Equal("calendar.upcoming", Assert.Single(result.ToolCalls).Name);
    }
}
