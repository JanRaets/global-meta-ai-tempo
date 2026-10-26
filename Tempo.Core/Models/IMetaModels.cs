namespace Tempo.Core.Models;

/// <summary>
/// The single abstraction over every model call (design D2). Implementations: MetaApiModels
/// (Muse Spark + Muse Voice Transcribe over the Meta Model API) and BasetenModels (Muse Spark
/// Open). No other model provider exists in this repo. The model never computes a time,
/// duration or coordinate — callers enforce that in tool guards, not prompts (design D1).
/// </summary>
public interface IMetaModels
{
    /// <summary>One assistant turn in a tool-calling loop (Muse Spark / Muse Spark Open).</summary>
    Task<ChatResult> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools,
        ChatCallOptions? options = null,
        CancellationToken ct = default);

    /// <summary>Typed object constrained by a JSON schema; one retry on parse failure, then <see cref="ModelOutputException"/>.</summary>
    Task<ModelResult<T>> ChatJsonAsync<T>(
        IReadOnlyList<ChatMessage> messages,
        string jsonSchema,
        string purpose = "unspecified",
        CancellationToken ct = default);

    /// <summary>Typed extraction from an image (Muse Spark vision).</summary>
    Task<ModelResult<T>> DescribeImageAsync<T>(
        ReadOnlyMemory<byte> image,
        string contentType,
        string instruction,
        string jsonSchema,
        string purpose = "unspecified",
        CancellationToken ct = default);

    /// <summary>Speech to text (Muse Voice Transcribe); audio up to 15 s, webm/opus or m4a.</summary>
    Task<TranscriptionResult> TranscribeAsync(
        ReadOnlyMemory<byte> audio,
        string contentType,
        CancellationToken ct = default);
}
