namespace Tempo.Core.Configuration;

/// <summary>Which model provider serves calls first; the other is the automatic fallback.</summary>
public enum ModelProvider
{
    MetaApi,
    Baseten,
}

public sealed class ModelsOptions
{
    public const string Section = "Models";

    public ModelProvider Primary { get; init; } = ModelProvider.MetaApi;
    public MetaApiOptions MetaApi { get; init; } = new();
    public BasetenOptions Baseten { get; init; } = new();
}

public sealed class MetaApiOptions
{
    public string ApiKey { get; init; } = "";
    public string BaseUrl { get; init; } = "https://api.llama.com/v1";
    public string ChatModel { get; init; } = "muse-spark";
    public string TranscribeModel { get; init; } = "muse-voice-transcribe";
}

public sealed class BasetenOptions
{
    public string ApiKey { get; init; } = "";
    public string BaseUrl { get; init; } = "https://inference.baseten.co/v1";
    public string Model { get; init; } = "meta-models/muse-spark-open-1026";
}

public sealed class RoutingOptions
{
    public const string Section = "Routing";

    /// <summary>OpenRouteService (design D15): Directions for estimates, Pelias endpoints for geocoding.</summary>
    public string BaseUrl { get; init; } = "https://api.openrouteservice.org";
    public string ApiKey { get; init; } = "";
    public int CacheMinutes { get; init; } = 5;
}

public sealed class TelegramOptions
{
    public const string Section = "Telegram";

    public string BotToken { get; init; } = "";
    public long ChatId { get; init; }
}

public sealed class GoogleOptions
{
    public const string Section = "Google";

    public string ClientId { get; init; } = "";
    public string ClientSecret { get; init; } = "";
}
