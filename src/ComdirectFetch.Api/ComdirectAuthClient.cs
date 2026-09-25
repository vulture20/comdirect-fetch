using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ComdirectFetch.Api;

/// <summary>
/// Implementiert den in KONZEPT.md Abschnitt 3 beschriebenen Authentifizierungsablauf:
/// Erst-Login → einmalige TAN-Freigabe → session-gebundener Token (cd_secondary-Flow) →
/// fortlaufender Refresh. Die genauen Endpunkt-Pfade und Header sind aus comdirect-
/// Community-Quellen und quelloffenen Clients rekonstruiert, nicht aus der offiziellen
/// Doku verifiziert (siehe KONZEPT.md Abschnitt 9, offener Punkt "Migrationswerkzeug"
/// und Quellenhinweise in Abschnitt 3) – vor dem ersten produktiven Lauf gegen die
/// aktuelle Swagger/Postman-Collection von developer.comdirect.de abgleichen.
/// </summary>
public sealed class ComdirectAuthClient(
    HttpClient httpClient,
    IOptions<ComdirectApiOptions> options,
    ICredentialProvider credentialProvider,
    ComdirectRequestContext requestContext,
    ILogger<ComdirectAuthClient> logger)
{
    private readonly ComdirectApiOptions _options = options.Value;

    /// <summary>Schritt 1: initialer Access-/Refresh-Token per Resource-Owner-Password-Flow.</summary>
    public async Task<OAuthToken> RequestInitialTokenAsync(CancellationToken cancellationToken = default)
    {
        var (username, password) = await credentialProvider.GetLoginCredentialsAsync(cancellationToken);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["username"] = username,
            ["password"] = password,
        };

        return await PostTokenAsync(form, cancellationToken);
    }

    /// <summary>Schritt 2a: legt eine neue Session an. Sicher wiederholbar (GET, gegen 429/5xx abgesichert).</summary>
    public async Task<SessionInfo> CreateSessionAsync(OAuthToken token, CancellationToken cancellationToken = default)
    {
        using var response = await ComdirectResilience.SendWithRetryAsync(
            httpClient,
            () => CreateRequest(HttpMethod.Get, "/api/session/clients/user/v1/sessions", token),
            cancellationToken);
        await response.EnsureSuccessWithBodyAsync(cancellationToken);

        var sessions = await response.Content.ReadFromJsonAsync<SessionInfo[]>(cancellationToken: cancellationToken);
        if (sessions is not { Length: > 0 })
        {
            throw new InvalidOperationException("comdirect hat keine Session zurückgegeben.");
        }

        return sessions[0];
    }

    /// <summary>
    /// Schritt 2b: stößt die TAN-Challenge an (löst z. B. eine PushTAN-Benachrichtigung aus).
    /// ACHTUNG (offizielle Doku, Kapitel 2.3): Fünf TAN-Challenges ohne zwischenzeitliche
    /// Einlösung einer korrekten TAN sperren den gesamten Online-Banking-Zugang, nicht nur
    /// den API-Zugriff. Diese Methode daher nicht unkontrolliert wiederholt aufrufen – siehe
    /// Absicherung in ComdirectAuthCoordinator.StartAsync. Bewusst OHNE automatischen
    /// Retry bei 429/5xx (siehe ComdirectResilience) – ein Retry würde hier ungewollt eine
    /// neue Challenge anfordern.
    /// </summary>
    public async Task<TanChallenge> RequestTanChallengeAsync(
        OAuthToken token,
        SessionInfo session,
        CancellationToken cancellationToken = default)
    {
        var path = $"/api/session/clients/user/v1/sessions/{session.Identifier}/validate";
        using var request = CreateRequest(HttpMethod.Post, path, token);
        // Laut offizieller Doku (Kapitel 2.3) muss der Body IMMER sessionTanActive/activated2FA
        // = true enthalten, unabhängig vom zuvor per GET abgerufenen Ist-Zustand.
        request.Content = JsonContent.Create(new SessionInfo
        {
            Identifier = session.Identifier,
            SessionTanActive = true,
            Activated2Fa = true,
        });

        using var response = await httpClient.SendAsync(request, cancellationToken);
        await response.EnsureSuccessWithBodyAsync(cancellationToken);

        if (!response.Headers.TryGetValues("x-once-authentication-info", out var values))
        {
            throw new InvalidOperationException(
                "Antwort auf die Session-Validierung enthielt keinen 'x-once-authentication-info'-Header.");
        }

        var challenge = JsonSerializer.Deserialize<TanChallenge>(values.First())
            ?? throw new InvalidOperationException("TAN-Challenge konnte nicht gelesen werden.");

        logger.LogInformation(
            "TAN-Freigabe erforderlich (Typ: {Typ}). Bitte in der comdirect-App bestätigen.",
            challenge.Typ);

        return challenge;
    }

    /// <summary>
    /// Schritt 2c: aktiviert die Session, nachdem die TAN in der App bestätigt wurde
    /// (PushTAN) bzw. mit manuell eingegebenem TAN-Code (photoTAN/mobileTAN).
    /// ACHTUNG (offizielle Doku, Kapitel 2.4): Drei falsche TAN-Eingaben sperren den
    /// gesamten Online-Banking-Zugang (nicht nur die API); nach zwei Fehlversuchen über die
    /// API lässt sich der Zähler nur durch eine korrekte TAN-Eingabe auf der comdirect-
    /// Website zurücksetzen. tanCode daher nur übergeben, wenn er wirklich vom Nutzer stammt.
    /// Bewusst OHNE automatischen Retry bei 429/5xx (siehe ComdirectResilience) – ein
    /// wiederholtes Einreichen des TAN-Codes ist hier riskant, nicht harmlos.
    /// </summary>
    public async Task ActivateSessionAsync(
        OAuthToken token,
        SessionInfo session,
        TanChallenge challenge,
        string? tanCode,
        CancellationToken cancellationToken = default)
    {
        var path = $"/api/session/clients/user/v1/sessions/{session.Identifier}";
        using var request = CreateRequest(HttpMethod.Patch, path, token);
        // Wie bei der Validierung: Body muss explizit sessionTanActive/activated2FA = true setzen.
        request.Content = JsonContent.Create(new SessionInfo
        {
            Identifier = session.Identifier,
            SessionTanActive = true,
            Activated2Fa = true,
        });
        request.Headers.Add("x-once-authentication-info", JsonSerializer.Serialize(new { id = challenge.Id }));
        if (!string.IsNullOrEmpty(tanCode))
        {
            request.Headers.Add("x-once-authentication", tanCode);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        await response.EnsureSuccessWithBodyAsync(cancellationToken);
    }

    /// <summary>
    /// Schritt 3: tauscht den durch die TAN freigegebenen Token gegen den session-gebundenen
    /// Token für die fachlichen Endpunkte (grant_type=cd_secondary).
    /// </summary>
    public async Task<OAuthToken> RequestSecondaryTokenAsync(
        OAuthToken initialToken,
        CancellationToken cancellationToken = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "cd_secondary",
            ["token"] = initialToken.AccessToken,
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
        };

        return await PostTokenAsync(form, cancellationToken, bearerToken: initialToken.AccessToken);
    }

    /// <summary>Erneuert einen bestehenden Token, ohne dass eine neue TAN nötig ist (solange rechtzeitig aufgerufen).</summary>
    public async Task<OAuthToken> RefreshTokenAsync(OAuthToken currentToken, CancellationToken cancellationToken = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = currentToken.RefreshToken,
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
        };

        return await PostTokenAsync(form, cancellationToken);
    }

    /// <summary>Token-Anfragen sind gefahrlos wiederholbar (kein TAN-Bezug) – abgesichert gegen 429/5xx.</summary>
    private async Task<OAuthToken> PostTokenAsync(
        Dictionary<string, string> form,
        CancellationToken cancellationToken,
        string? bearerToken = null)
    {
        HttpRequestMessage CreateTokenRequest()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl}/oauth/token")
            {
                Content = new FormUrlEncodedContent(form),
            };
            if (bearerToken is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            }

            return request;
        }

        using var response = await ComdirectResilience.SendWithRetryAsync(httpClient, CreateTokenRequest, cancellationToken);
        await response.EnsureSuccessWithBodyAsync(cancellationToken);

        return await response.Content.ReadFromJsonAsync<OAuthToken>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("comdirect hat keinen Token zurückgegeben.");
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, OAuthToken token)
    {
        var request = new HttpRequestMessage(method, $"{_options.BaseUrl}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("x-http-request-info", requestContext.BuildRequestInfoHeader());
        return request;
    }
}
