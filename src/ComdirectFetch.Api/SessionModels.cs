using System.Text.Json.Serialization;

namespace ComdirectFetch.Api;

/// <summary>Session-Objekt von GET/POST/PATCH .../session/clients/user/v1/sessions (KONZEPT.md Abschnitt 3).</summary>
public sealed class SessionInfo
{
    [JsonPropertyName("identifier")]
    public required string Identifier { get; init; }

    [JsonPropertyName("sessionTanActive")]
    public bool SessionTanActive { get; init; }

    [JsonPropertyName("activated2FA")]
    public bool Activated2Fa { get; init; }
}

/// <summary>
/// TAN-Challenge aus dem "x-once-authentication-info"-Response-Header der Session-Validierung.
/// Typische Werte für <see cref="Typ"/>: P_TAN, P_TAN_PUSH, M_TAN.
/// </summary>
public sealed class TanChallenge
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("typ")]
    public string? Typ { get; init; }

    [JsonPropertyName("challenge")]
    public string? Challenge { get; init; }
}
