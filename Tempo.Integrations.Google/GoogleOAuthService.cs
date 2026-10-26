using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Tempo.Core.Configuration;

namespace Tempo.Integrations.Google;

/// <summary>Hand-rolled OAuth for the one flow we need: consent (readonly scope), code
/// exchange, refresh. No Google SDK, no write scope.</summary>
public sealed class GoogleOAuthService(HttpClient http, IOptions<GoogleOptions> options, GoogleTokenStore tokenStore)
{
    public const string Scope = "https://www.googleapis.com/auth/calendar.readonly";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    private string? cachedAccessToken;
    private DateTimeOffset cachedAccessTokenExpiry;

    /// <summary>The consent URL requests only calendar.readonly (calendar-sync spec).</summary>
    public static string BuildConsentUrl(GoogleOptions options, string state) =>
        "https://accounts.google.com/o/oauth2/v2/auth"
        + $"?client_id={Uri.EscapeDataString(options.ClientId)}"
        + $"&redirect_uri={Uri.EscapeDataString(options.RedirectUri)}"
        + "&response_type=code"
        + $"&scope={Uri.EscapeDataString(Scope)}"
        + "&access_type=offline"
        + "&prompt=consent"
        + $"&state={Uri.EscapeDataString(state)}";

    public string BuildConsentUrl(string state) => BuildConsentUrl(options.Value, state);

    /// <summary>Exchanges the callback code and stores the refresh token encrypted.</summary>
    public async Task ExchangeCodeAsync(string code, CancellationToken ct = default)
    {
        var response = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = options.Value.ClientId,
            ["client_secret"] = options.Value.ClientSecret,
            ["redirect_uri"] = options.Value.RedirectUri,
            ["grant_type"] = "authorization_code",
        }), ct);
        var body = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct);
        if (!response.IsSuccessStatusCode || body?.RefreshToken is null or "")
            throw new InvalidOperationException(
                $"Google code exchange failed ({(int)response.StatusCode}): {body?.Error ?? "no refresh_token in response"}");
        await tokenStore.SaveRefreshTokenAsync(body.RefreshToken, ct);
    }

    /// <summary>Access token from the stored refresh token, cached until shortly before expiry.</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (cachedAccessToken is not null && DateTimeOffset.UtcNow < cachedAccessTokenExpiry)
            return cachedAccessToken;
        var refreshToken = await tokenStore.GetRefreshTokenAsync(ct)
            ?? throw new InvalidOperationException("Google Calendar is not connected (no refresh token stored)");
        var response = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["refresh_token"] = refreshToken,
            ["client_id"] = options.Value.ClientId,
            ["client_secret"] = options.Value.ClientSecret,
            ["grant_type"] = "refresh_token",
        }), ct);
        var body = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct);
        if (!response.IsSuccessStatusCode || body?.AccessToken is null or "")
            throw new InvalidOperationException($"Google token refresh failed ({(int)response.StatusCode}): {body?.Error}");
        cachedAccessToken = body.AccessToken;
        cachedAccessTokenExpiry = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, body.ExpiresIn - 60));
        return cachedAccessToken;
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("error")] string? Error);
}
