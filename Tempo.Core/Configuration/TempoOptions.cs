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
    public string BaseUrl { get; init; } = "https://api.meta.ai/v1";
    public string ChatModel { get; init; } = "muse-spark-1.3";
    public string TranscribeModel { get; init; } = "muse-voice-transcribe-1.0";
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

/// <summary>Web Push (design D16). VAPID keys live in user-secrets, never in the repo.</summary>
public sealed class PushOptions
{
    public const string Section = "Push";

    /// <summary>VAPID contact, sent to the push services (mailto: or https: URL).</summary>
    public string Subject { get; init; } = "mailto:tempo@personalbot.nl";
    public string VapidPublicKey { get; init; } = "";
    public string VapidPrivateKey { get; init; } = "";
    /// <summary>Base delay for transient-error retries; doubled per attempt. 0 in tests.</summary>
    public int RetryBaseMs { get; init; } = 500;
}

public sealed class GoogleOptions
{
    public const string Section = "Google";

    public string ClientId { get; init; } = "";
    public string ClientSecret { get; init; } = "";
    public string RedirectUri { get; init; } = "http://localhost:5181/auth/google/callback";
}
