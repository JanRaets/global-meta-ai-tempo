using Tempo.Core.Configuration;

namespace Tempo.Core.Models;

public enum ChatRole
{
    System,
    User,
    Assistant,
    Tool,
}

/// <summary>One turn in a chat. Tool results are messages with Role=Tool and the ToolCallId they answer.</summary>
public sealed record ChatMessage(ChatRole Role, string Content, IReadOnlyList<ToolCall>? ToolCalls = null, string? ToolCallId = null)
{
    public static ChatMessage System(string content) => new(ChatRole.System, content);
    public static ChatMessage User(string content) => new(ChatRole.User, content);
    public static ChatMessage Assistant(string content, IReadOnlyList<ToolCall>? toolCalls = null) => new(ChatRole.Assistant, content, toolCalls);
    public static ChatMessage ToolResult(string toolCallId, string contentJson) => new(ChatRole.Tool, contentJson, ToolCallId: toolCallId);
}

/// <summary>A tool the model may call: name, description and a JSON schema for the arguments.</summary>
public sealed record ToolDefinition(string Name, string Description, string ParametersJsonSchema);

/// <summary>A tool call the model made; arguments are raw JSON the tool layer parses and validates.</summary>
public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

public sealed record ChatCallOptions
{
    public string Purpose { get; init; } = "unspecified";
    public double? Temperature { get; init; }
    public int? MaxTokens { get; init; }
}

/// <summary>An assistant turn: either tool calls to execute, or final text (never both treated as final).</summary>
public sealed record ChatResult(string? Text, IReadOnlyList<ToolCall> ToolCalls, ModelProvider Provider)
{
    public bool IsFinal => ToolCalls.Count == 0;
}

/// <summary>A typed model result plus which provider actually served it.</summary>
public sealed record ModelResult<T>(T Value, ModelProvider Provider);

public sealed record TranscriptionResult(string Text, string Language, ModelProvider Provider);
