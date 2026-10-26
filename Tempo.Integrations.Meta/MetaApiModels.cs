using System.Diagnostics;
using System.Net.Http.Json;
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

    public async Task<TranscriptionResult> TranscribeAsync(
        ReadOnlyMemory<byte> audio, string contentType, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var success = false;
        try
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(audio.ToArray());
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            form.Add(file, "file", contentType.Contains("webm") ? "audio.webm" : "audio.m4a");
            form.Add(new StringContent(options.TranscribeModel), "model");
            form.Add(new StringContent("verbose_json"), "response_format");

            HttpResponseMessage response;
            try
            {
                response = await http.PostAsync("audio/transcriptions", form, ct);
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

            var parsed = await response.Content.ReadFromJsonAsync<WireTranscription>(Wire.Json, ct)
                ?? throw new ModelHttpException(502, "MetaApi transcription returned an empty body");
            success = true;
            return new TranscriptionResult(parsed.Text ?? "", parsed.Language ?? "unknown", ModelProvider.MetaApi);
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
