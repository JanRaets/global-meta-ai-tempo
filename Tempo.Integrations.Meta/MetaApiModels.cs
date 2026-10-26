using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tempo.Core.Configuration;
using Tempo.Core.Models;

namespace Tempo.Integrations.Meta;

/// <summary>Muse Spark (chat, vision) and Muse Voice Transcribe over the Meta Model API.</summary>
public sealed class MetaApiModels : ChatCompletionsClient, IMetaModels
{
    private readonly HttpClient http;
    private readonly IModelCallLog log;
    private readonly ILogger<MetaApiModels> logger;
    private readonly MetaApiOptions options;

    public MetaApiModels(HttpClient http, IOptions<ModelsOptions> options, IModelCallLog log, ILogger<MetaApiModels> logger)
        : base(http, log, logger)
    {
        this.http = http;
        this.log = log;
        this.logger = logger;
        this.options = options.Value.MetaApi;
    }

    protected override ModelProvider Provider => ModelProvider.MetaApi;
    protected override string ChatModel => options.ChatModel;

    /// <summary>POST /v1/asr/transcribe: multipart "request" (JSON settings) + "audio" part.
    /// The API accepts WAV only (mono PCM 16/24 kHz) and returns no detected language
    /// (design.md D2 API facts); Language is always "unknown".</summary>
    public async Task<TranscriptionResult> TranscribeAsync(
        ReadOnlyMemory<byte> audio, string contentType, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var success = false;
        try
        {
            using var form = new MultipartFormDataContent();
            var settings = new WireTranscribeRequest(options.TranscribeModel, "WAV", "PUSH_TO_TALK");
            var requestPart = new StringContent(JsonSerializer.Serialize(settings, Wire.Json));
            requestPart.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            form.Add(requestPart, "request");
            var audioPart = new ByteArrayContent(audio.ToArray());
            audioPart.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(audioPart, "audio", "audio.wav");

            HttpResponseMessage response;
            try
            {
                response = await http.PostAsync("asr/transcribe", form, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new ModelHttpException(503, $"MetaApi transcription unreachable: {ex.Message}");
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new ModelHttpException((int)response.StatusCode,
                    $"MetaApi transcription returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 300)]}");
            }

            var parsed = await response.Content.ReadFromJsonAsync<WireTranscribeResponse>(Wire.Json, ct)
                ?? throw new ModelHttpException(502, "MetaApi transcription returned an empty body");
            success = true;
            return new TranscriptionResult(parsed.Transcript ?? "", "unknown", ModelProvider.MetaApi);
        }
        finally
        {
            log.Record(new ModelCallRecord(ModelProvider.MetaApi, options.TranscribeModel, "transcription",
                null, null, stopwatch.ElapsedMilliseconds, success, DateTimeOffset.UtcNow));
            logger.LogInformation("Model call MetaApi/{Model} purpose=transcription success={Success} latencyMs={Latency}",
                options.TranscribeModel, success, stopwatch.ElapsedMilliseconds);
        }
    }
}
