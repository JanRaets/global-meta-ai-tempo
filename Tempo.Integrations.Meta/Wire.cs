using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tempo.Integrations.Meta;

// Wire DTOs for the chat-completions-style HTTP API that both the Meta Model API and
// Baseten expose. Raw JSON over HttpClient — deliberately no provider SDK.

internal static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

internal sealed record WireChatRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<WireMessage> Messages,
    [property: JsonPropertyName("tools")] IReadOnlyList<WireTool>? Tools = null,
    [property: JsonPropertyName("response_format")] WireResponseFormat? ResponseFormat = null,
    [property: JsonPropertyName("temperature")] double? Temperature = null,
    [property: JsonPropertyName("max_tokens")] int? MaxTokens = null);

internal sealed record WireMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] object? Content,
    [property: JsonPropertyName("tool_calls")] IReadOnlyList<WireToolCall>? ToolCalls = null,
    [property: JsonPropertyName("tool_call_id")] string? ToolCallId = null);

internal sealed record WireContentText(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text);

internal sealed record WireContentImage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("image_url")] WireImageUrl ImageUrl);

internal sealed record WireImageUrl([property: JsonPropertyName("url")] string Url);

internal sealed record WireTool(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] WireFunction Function);

internal sealed record WireFunction(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("parameters")] JsonElement? Parameters = null,
    [property: JsonPropertyName("arguments")] string? Arguments = null);

internal sealed record WireResponseFormat(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("json_schema")] WireJsonSchema? JsonSchema = null);

// strict=false: output is schema-constrained either way, and strict mode demands the
// additionalProperties:false / all-required subset our schemas don't commit to.
internal sealed record WireJsonSchema(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("schema")] JsonElement Schema,
    [property: JsonPropertyName("strict")] bool Strict = false);

internal sealed record WireToolCall(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("function")] WireFunction Function);

internal sealed record WireChatResponse(
    [property: JsonPropertyName("choices")] IReadOnlyList<WireChoice>? Choices,
    [property: JsonPropertyName("usage")] WireUsage? Usage);

internal sealed record WireChoice(
    [property: JsonPropertyName("message")] WireMessageOut? Message,
    [property: JsonPropertyName("finish_reason")] string? FinishReason);

internal sealed record WireMessageOut(
    [property: JsonPropertyName("content")] string? Content,
    [property: JsonPropertyName("tool_calls")] IReadOnlyList<WireToolCall>? ToolCalls);

internal sealed record WireUsage(
    [property: JsonPropertyName("prompt_tokens")] int? PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int? CompletionTokens);

// POST /v1/asr/transcribe: multipart with a JSON "request" part and an "audio" part (WAV only).
internal sealed record WireTranscribeRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("audioEncoding")] string AudioEncoding,
    [property: JsonPropertyName("mode")] string Mode);

// TranscribeResponse carries no detected-language field (see design.md D2 API facts).
internal sealed record WireTranscribeResponse(
    [property: JsonPropertyName("sessionId")] string? SessionId,
    [property: JsonPropertyName("transcript")] string? Transcript,
    [property: JsonPropertyName("audioDurationMs")] long? AudioDurationMs);
