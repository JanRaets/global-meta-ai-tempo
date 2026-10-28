using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tempo.Core.Configuration;
using Tempo.Core.Models;

namespace Tempo.Integrations.Meta;

/// <summary>Muse Spark Open (meta-models/muse-spark-open-1026) on Baseten.
/// Transcription delegates to the Meta API — Baseten serves no speech model here.</summary>
public sealed class BasetenModels : ChatCompletionsClient, IMetaModels
{
    private readonly MetaApiModels metaApi;
    private readonly BasetenOptions options;

    public BasetenModels(HttpClient http, IOptions<ModelsOptions> options, MetaApiModels metaApi,
        IModelCallLog log, ILogger<BasetenModels> logger)
        : base(http, log, logger)
    {
        this.metaApi = metaApi;
        this.options = options.Value.Baseten;
    }

    protected override ModelProvider Provider => ModelProvider.Baseten;
    protected override string ChatModel => options.Model;

    // Baseten validates function names against [a-zA-Z0-9_-] (observed live, 28 Oct 2026:
    // 400 'Function at index 0 has an invalid name: "calendar.upcoming"'). Our tool names
    // contain dots and no underscores, so the swap is bijective both ways.
    protected override string ToolNameToWire(string name) => name.Replace('.', '_');
    protected override string ToolNameFromWire(string name) => name.Replace('_', '.');

    public Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<byte> audio, string contentType, CancellationToken ct = default)
        => metaApi.TranscribeAsync(audio, contentType, ct);
}
