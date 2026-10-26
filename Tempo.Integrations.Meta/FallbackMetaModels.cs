using Microsoft.Extensions.Logging;
using Tempo.Core.Configuration;
using Tempo.Core.Models;

namespace Tempo.Integrations.Meta;

/// <summary>
/// Serves every call from the primary provider and retries a failed chat or vision call once
/// on the fallback when the primary returns 429 or 5xx (meta-models spec: Automatic fallback).
/// Each result records which provider actually served it.
/// </summary>
public sealed class FallbackMetaModels(
    IMetaModels primary,
    IMetaModels fallback,
    ModelProvider primaryProvider,
    ModelProvider fallbackProvider,
    ILogger<FallbackMetaModels> logger) : IMetaModels
{
    public ModelProvider PrimaryProvider { get; } = primaryProvider;
    public ModelProvider FallbackProvider { get; } = fallbackProvider;

    public Task<ChatResult> ChatAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools,
        ChatCallOptions? options = null, CancellationToken ct = default)
        => WithFallback(m => m.ChatAsync(messages, tools, options, ct));

    public Task<ModelResult<T>> ChatJsonAsync<T>(IReadOnlyList<ChatMessage> messages, string jsonSchema,
        string purpose = "unspecified", CancellationToken ct = default)
        => WithFallback(m => m.ChatJsonAsync<T>(messages, jsonSchema, purpose, ct));

    public Task<ModelResult<T>> DescribeImageAsync<T>(ReadOnlyMemory<byte> image, string contentType,
        string instruction, string jsonSchema, string purpose = "unspecified", CancellationToken ct = default)
        => WithFallback(m => m.DescribeImageAsync<T>(image, contentType, instruction, jsonSchema, purpose, ct));

    // Transcription has one home (Muse Voice Transcribe on the Meta API); no cross-provider fallback.
    public Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<byte> audio, string contentType, CancellationToken ct = default)
        => primary.TranscribeAsync(audio, contentType, ct);

    private async Task<T> WithFallback<T>(Func<IMetaModels, Task<T>> call)
    {
        try
        {
            return await call(primary);
        }
        catch (ModelHttpException ex) when (ex.IsFallbackEligible)
        {
            logger.LogWarning("Primary {Primary} failed ({Status}); retrying on {Fallback}",
                PrimaryProvider, ex.StatusCode, FallbackProvider);
            try
            {
                return await call(fallback);
            }
            catch (ModelHttpException fex)
            {
                throw new ModelUnavailableException(
                    $"Both providers failed: {PrimaryProvider} ({ex.StatusCode}), {FallbackProvider} ({fex.StatusCode})", fex);
            }
        }
    }
}
