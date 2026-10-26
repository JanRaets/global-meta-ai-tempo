using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tempo.Core.Configuration;
using Tempo.Core.Models;

namespace Tempo.Integrations.Meta;

/// <summary>
/// Shared HTTP plumbing for the chat-completions-style API both providers speak.
/// Subclasses supply the provider identity and model names.
/// </summary>
public abstract class ChatCompletionsClient(HttpClient http, IModelCallLog log, ILogger logger)
{
    protected abstract ModelProvider Provider { get; }
    protected abstract string ChatModel { get; }

    private protected async Task<(WireMessageOut Message, WireUsage? Usage)> SendChatAsync(
        IReadOnlyList<WireMessage> messages,
        IReadOnlyList<WireTool>? tools,
        WireResponseFormat? responseFormat,
        ChatCallOptions options,
        CancellationToken ct)
    {
        var request = new WireChatRequest(ChatModel, messages, tools, responseFormat, options.Temperature, options.MaxTokens);
        var stopwatch = Stopwatch.StartNew();
        WireUsage? usage = null;
        var success = false;
        try
        {
            HttpResponseMessage response;
            try
            {
                response = await http.PostAsJsonAsync("chat/completions", request, Wire.Json, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new ModelHttpException(503, $"{Provider} unreachable: {ex.Message}");
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new ModelHttpException((int)response.StatusCode,
                    $"{Provider} chat returned {(int)response.StatusCode}: {Truncate(body)}");
            }

            var parsed = await response.Content.ReadFromJsonAsync<WireChatResponse>(Wire.Json, ct)
                ?? throw new ModelHttpException(502, $"{Provider} chat returned an empty body");
            var message = parsed.Choices?.FirstOrDefault()?.Message
                ?? throw new ModelHttpException(502, $"{Provider} chat returned no choices");
            usage = parsed.Usage;
            success = true;
            return (message, usage);
        }
        finally
        {
            log.Record(new ModelCallRecord(Provider, ChatModel, options.Purpose,
                usage?.PromptTokens, usage?.CompletionTokens, stopwatch.ElapsedMilliseconds, success, DateTimeOffset.UtcNow));
            logger.LogInformation("Model call {Provider}/{Model} purpose={Purpose} success={Success} latencyMs={Latency}",
                Provider, ChatModel, options.Purpose, success, stopwatch.ElapsedMilliseconds);
        }
    }

    public async Task<ChatResult> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        ChatCallOptions? options = null,
        CancellationToken ct = default)
    {
        var wireTools = tools.Count == 0
            ? null
            : tools.Select(t => new WireTool("function",
                new WireFunction(t.Name, t.Description, JsonDocument.Parse(t.ParametersJsonSchema).RootElement))).ToList();
        var (message, _) = await SendChatAsync(ToWire(messages), wireTools, null, options ?? new ChatCallOptions(), ct);
        var toolCalls = (message.ToolCalls ?? [])
            .Select(c => new ToolCall(c.Id, c.Function.Name, c.Function.Arguments ?? "{}"))
            .ToList();
        return new ChatResult(message.Content, toolCalls, Provider);
    }

    public async Task<ModelResult<T>> ChatJsonAsync<T>(
        IReadOnlyList<ChatMessage> messages,
        string jsonSchema,
        string purpose = "unspecified",
        CancellationToken ct = default)
        => await CompleteJsonAsync<T>(ToWire(messages), jsonSchema, purpose, ct);

    public async Task<ModelResult<T>> DescribeImageAsync<T>(
        ReadOnlyMemory<byte> image,
        string contentType,
        string instruction,
        string jsonSchema,
        string purpose = "unspecified",
        CancellationToken ct = default)
    {
        var dataUri = $"data:{contentType};base64,{Convert.ToBase64String(image.Span)}";
        var content = new object[]
        {
            new WireContentText("text", instruction),
            new WireContentImage("image_url", new WireImageUrl(dataUri)),
        };
        var messages = new List<WireMessage> { new("user", content) };
        return await CompleteJsonAsync<T>(messages, jsonSchema, purpose, ct);
    }

    /// <summary>Schema-constrained completion with one retry on parse failure (meta-models spec: Structured output).</summary>
    private async Task<ModelResult<T>> CompleteJsonAsync<T>(
        IReadOnlyList<WireMessage> messages, string jsonSchema, string purpose, CancellationToken ct)
    {
        var responseFormat = new WireResponseFormat("json_schema",
            new WireJsonSchema(typeof(T).Name, JsonDocument.Parse(jsonSchema).RootElement));
        var options = new ChatCallOptions { Purpose = purpose };
        var attemptMessages = messages;
        Exception? firstFailure = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var (message, _) = await SendChatAsync(attemptMessages, null, responseFormat, options, ct);
            try
            {
                var value = JsonSerializer.Deserialize<T>(message.Content ?? "", Wire.Json);
                if (value is null) throw new JsonException("null result");
                return new ModelResult<T>(value, Provider);
            }
            catch (JsonException ex)
            {
                firstFailure ??= ex;
                logger.LogWarning("Model JSON parse failure (attempt {Attempt}) for {Type}: {Message}", attempt, typeof(T).Name, ex.Message);
                attemptMessages =
                [
                    .. messages,
                    new WireMessage("assistant", message.Content),
                    new WireMessage("user", "That was not valid JSON matching the schema. Reply with only a single valid JSON object."),
                ];
            }
        }
        throw new ModelOutputException($"Model output could not be parsed as {typeof(T).Name} after one retry", firstFailure);
    }

    private static IReadOnlyList<WireMessage> ToWire(IReadOnlyList<ChatMessage> messages) =>
        messages.Select(m => new WireMessage(
            m.Role switch
            {
                ChatRole.System => "system",
                ChatRole.User => "user",
                ChatRole.Assistant => "assistant",
                ChatRole.Tool => "tool",
                _ => throw new ArgumentOutOfRangeException(nameof(messages), m.Role, "unknown role"),
            },
            m.Content,
            m.ToolCalls?.Select(c => new WireToolCall(c.Id, "function", new WireFunction(c.Name, Arguments: c.ArgumentsJson))).ToList(),
            m.ToolCallId)).ToList();

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];
}
