using System.Text.Json.Serialization;

namespace ComdirectFetch.Api;

/// <summary>
/// Antwort von POST /oauth/token. Feldnamen gemäß comdirect REST API Dokumentation
/// (KONZEPT.md Abschnitt 3) – vor dem ersten produktiven Lauf gegen die aktuelle
/// offizielle Doku/Postman-Collection zu verifizieren.
/// </summary>
public sealed class OAuthToken
{
    [JsonPropertyName("access_token")]
    public required string AccessToken { get; init; }

    [JsonPropertyName("refresh_token")]
    public required string RefreshToken { get; init; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; init; }

    [JsonPropertyName("expires_in")]
    public int ExpiresInSeconds { get; init; }

    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    /// <summary>Zeitpunkt, ab dem dieser Token sicherheitshalber erneuert werden sollte (siehe TokenRefreshService).</summary>
    public DateTimeOffset ObtainedAt { get; init; } = DateTimeOffset.UtcNow;
}
