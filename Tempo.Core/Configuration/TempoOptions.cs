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
    /// <summary>For the /admin/model-usage spend estimate; 0 shows token counts only.</summary>
    public double EstimatedUsdPer1MPromptTokens { get; init; }
    public double EstimatedUsdPer1MCompletionTokens { get; init; }
}

/// <summary>Agent loop limits (13.c): the tick already avoids redundant runs; the daily cap
/// is the hard stop against a runaway loop burning credits.</summary>
public sealed class AgentOptions
{
    public const string Section = "Agent";

    public int MaxRunsPerDay { get; init; } = 200;
}

/// <summary>Plan phase thresholds (design D6). Forgiving on purpose: Owntracks significant
/// mode is coarse; the PWA's watchPosition is the fine-grained signal while open.</summary>
public sealed class PhaseOptions
{
    public const string Section = "Phase";

    /// <summary>Moved this far from the leave reference → EnRoute.</summary>
    public int EnRouteMeters { get; init; } = 150;

    /// <summary>Within this of the destination → Arrived, or AtDestination before start
    /// (the spec's "ArriveRadiusM"). Also the same-place threshold for chained origins.</summary>
    public int ArrivedMeters { get; init; } = 100;

    /// <summary>Chained origins: a look-ahead plan departs from the previous placed
    /// appointment when it ends no more than this many hours before this one starts.</summary>
    public int ChainGapHours { get; init; } = 3;

    /// <summary>EnRoute from movement (28 Oct 2026): two consecutive fixes within
    /// MovementWindowMinutes that are more than this far apart mean the user is moving.</summary>
    public int MovementMeters { get; init; } = 300;

    /// <summary>The pair window for the movement rule.</summary>
    public int MovementWindowMinutes { get; init; } = 10;

    /// <summary>EnRoute when the current fix is this much closer to the destination
    /// than the leave point.</summary>
    public int ApproachMeters { get; init; } = 500;

    /// <summary>No fix proof needed: this long after the appointment start → Arrived.</summary>
    public int ArrivedFallbackMin { get; init; } = 15;
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
    /// <summary>Pelias boundary.country for geocoding; empty disables the filter.</summary>
    public string GeocodeCountry { get; init; } = "NL";

    /// <summary>D18: when the provider is down for every mode, plan from a straight-line
    /// estimate (marked and worded "roughly") instead of producing no options.</summary>
    public bool FallbackEnabled { get; init; } = true;

    /// <summary>Traffic-aware car routing (added 28 Oct 2026): which provider routes cars.</summary>
    public CarRoutingOptions Car { get; init; } = new();
    public TomTomOptions TomTom { get; init; } = new();
}

public sealed class CarRoutingOptions
{
    /// <summary>"TomTom" (live traffic, ORS in-flight fallback) or "Ors".</summary>
    public string Provider { get; init; } = "TomTom";
}

public sealed class TomTomOptions
{
    public string BaseUrl { get; init; } = "https://api.tomtom.com";
    public string ApiKey { get; init; } = "";
    /// <summary>Free-tier routing calls per day; past it the composite falls back to ORS.</summary>
    public int DailyLimit { get; init; } = 2500;
}

/// <summary>Web Push (design D16). VAPID keys live in user-secrets, never in the repo.</summary>
public sealed class PushOptions
{
    public const string Section = "Push";

    /// <summary>VAPID contact, sent to the push services (mailto: or https: URL).</summary>
    public string Subject { get; init; } = "mailto:tempo@personalbot.dev";
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
