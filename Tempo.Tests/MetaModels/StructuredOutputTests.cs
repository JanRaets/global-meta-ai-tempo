using System.Net;
using Tempo.Core.Models;
using Tempo.Integrations.Meta;
using Tempo.Tests.Fakes;

namespace Tempo.Tests.MetaModels;

public sealed record Probe(string Name);

public sealed class StructuredOutputTests
{
    private const string Schema = """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""";
    private static readonly IReadOnlyList<ChatMessage> Messages = [ChatMessage.User("extract")];

    [Fact]
    public async Task Invalid_json_once_is_retried_and_parsed()
    {
        var handler = TestClients.ChatText("definitely not json", """{"name":"x"}""");
        var models = TestClients.MetaApi(handler);

        var result = await models.ChatJsonAsync<Probe>(Messages, Schema);

        Assert.Equal("x", result.Value.Name);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Invalid_json_twice_throws_and_no_partial_result_is_used()
    {
        var handler = TestClients.ChatText("not json", "still not json");
        var models = TestClients.MetaApi(handler);

        await Assert.ThrowsAsync<ModelOutputException>(() => models.ChatJsonAsync<Probe>(Messages, Schema));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Tool_call_is_returned_typed_and_text_is_not_final()
    {
        var body = """
            {"choices":[{"message":{"content":"thinking…","tool_calls":[
              {"id":"c1","type":"function","function":{"name":"plan.options","arguments":"{\"appointmentId\":\"a1\"}"}}
            ]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":1,"completion_tokens":1}}
            """;
        var models = TestClients.MetaApi(new StubHttpHandler((HttpStatusCode.OK, body)));

        var result = await models.ChatAsync(Messages, [new ToolDefinition("plan.options", "options", """{"type":"object"}""")]);

        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("plan.options", call.Name);
        Assert.Contains("a1", call.ArgumentsJson);
        Assert.False(result.IsFinal);
    }

    [Fact]
    public async Task Transcription_returns_text_with_detected_language()
    {
        var handler = new StubHttpHandler((HttpStatusCode.OK, """{"text":"ik ga eerst even naar de Albert Heijn","language":"nl"}"""));
        var models = TestClients.MetaApi(handler);

        var result = await models.TranscribeAsync(new byte[] { 1, 2, 3 }, "audio/webm");

        Assert.Equal("nl", result.Language);
        Assert.Contains("Albert Heijn", result.Text);
    }

    [Fact]
    public async Task Every_call_is_accounted_with_provider_and_purpose()
    {
        var log = new InMemoryModelCallLog();
        var models = TestClients.MetaApi(TestClients.ChatText("""{"name":"x"}"""), log);

        await models.ChatJsonAsync<Probe>(Messages, Schema, purpose: "habit-wording");

        var summary = Assert.Single(log.Summarize());
        Assert.Equal("habit-wording", summary.Purpose);
        Assert.Equal(Tempo.Core.Configuration.ModelProvider.MetaApi, summary.Provider);
        Assert.Equal(1, summary.Calls);
        Assert.Equal(10, summary.PromptTokens);
    }
}
