using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ComdirectFetch.Api;

/// <summary>
/// comdirect verlangt auf den fachlichen Endpunkten (nicht auf /oauth/token) einen
/// "x-http-request-info"-Header mit einer über den gesamten Client-Lauf stabilen
/// sessionId sowie einer je Aufruf neuen requestId (KONZEPT.md Abschnitt 3, Quelle:
/// comdirect-Community/quelloffene Clients – vor Produktivbetrieb gegen die offizielle
/// Doku zu verifizieren).
/// </summary>
public sealed class ComdirectRequestContext
{
    /// <summary>32-stellige Hex-ID, über die gesamte Laufzeit der Anwendung stabil.</summary>
    public string SessionId { get; } = RandomNumberGenerator.GetHexString(32, lowercase: true);

    /// <summary>Liefert eine neue, 9-stellige requestId (HHmmssfff) für den nächsten API-Aufruf.</summary>
    public string NextRequestId() => DateTime.UtcNow.ToString("HHmmssfff");

    public string BuildRequestInfoHeader()
    {
        var payload = new { clientRequestId = new { sessionId = SessionId, requestId = NextRequestId() } };
        return JsonSerializer.Serialize(payload);
    }
}
