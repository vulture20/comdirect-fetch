using System.Threading;
using ComdirectFetch.Api;

namespace ComdirectFetch.Worker.Services;

public enum AuthState
{
    NichtAuthentifiziert,
    TanAusstehend,
    Authentifiziert
}

/// <summary>
/// Hält den Authentifizierungs-Zustand des laufenden Diensts (KONZEPT.md Abschnitt 3) und
/// orchestriert den mehrstufigen Ablauf. Wird von den /auth/*-Endpunkten (manuelle
/// TAN-Freigabe) und vom TokenRefreshBackgroundService (automatischer Refresh) verwendet.
/// Als einfachste erste Variante hält der Coordinator seinen Zustand nur im Prozessspeicher;
/// nach einem Neustart ist daher in jedem Fall eine neue TAN-Freigabe nötig.
/// </summary>
public sealed class ComdirectAuthCoordinator(
    ComdirectAuthClient authClient,
    ILogger<ComdirectAuthCoordinator> logger)
{
    private readonly Lock _lock = new();
    private OAuthToken? _initialToken;
    private SessionInfo? _session;
    private TanChallenge? _pendingChallenge;

    public AuthState State { get; private set; } = AuthState.NichtAuthentifiziert;
    public OAuthToken? CurrentToken { get; private set; }

    /// <summary>
    /// ACHTUNG: comdirect sperrt nach fünf TAN-Challenges ohne zwischenzeitliche Einlösung
    /// einer korrekten TAN den gesamten Online-Banking-Zugang (siehe ComdirectAuthClient).
    /// Ist bereits eine Freigabe ausstehend, wird deshalb keine neue Challenge angefordert,
    /// sondern die bestehende zurückgegeben.
    /// </summary>
    public async Task<TanChallenge> StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (State == AuthState.TanAusstehend && _pendingChallenge is not null)
            {
                return _pendingChallenge;
            }
        }

        var token = await authClient.RequestInitialTokenAsync(cancellationToken);
        var session = await authClient.CreateSessionAsync(token, cancellationToken);
        var challenge = await authClient.RequestTanChallengeAsync(token, session, cancellationToken);

        lock (_lock)
        {
            _initialToken = token;
            _session = session;
            _pendingChallenge = challenge;
            State = AuthState.TanAusstehend;
        }

        return challenge;
    }

    public async Task ConfirmAsync(string? tanCode, CancellationToken cancellationToken = default)
    {
        OAuthToken initialToken;
        SessionInfo session;
        TanChallenge challenge;
        lock (_lock)
        {
            if (State != AuthState.TanAusstehend || _initialToken is null || _session is null || _pendingChallenge is null)
            {
                throw new InvalidOperationException("Keine TAN-Freigabe ausstehend. Zuerst /auth/start aufrufen.");
            }

            initialToken = _initialToken;
            session = _session;
            challenge = _pendingChallenge;
        }

        await authClient.ActivateSessionAsync(initialToken, session, challenge, tanCode, cancellationToken);
        var secondaryToken = await authClient.RequestSecondaryTokenAsync(initialToken, cancellationToken);

        lock (_lock)
        {
            CurrentToken = secondaryToken;
            State = AuthState.Authentifiziert;
            _pendingChallenge = null;
        }

        logger.LogInformation("comdirect-Session erfolgreich freigegeben.");
    }

    /// <summary>Wird periodisch vom TokenRefreshBackgroundService aufgerufen (KONZEPT.md Abschnitt 3).</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        OAuthToken? current;
        lock (_lock)
        {
            current = CurrentToken;
        }

        if (current is null)
        {
            return;
        }

        try
        {
            var refreshed = await authClient.RefreshTokenAsync(current, cancellationToken);
            lock (_lock)
            {
                CurrentToken = refreshed;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Token-Refresh fehlgeschlagen – Session vermutlich abgelaufen. Neue TAN-Freigabe erforderlich.");
            lock (_lock)
            {
                CurrentToken = null;
                State = AuthState.NichtAuthentifiziert;
            }

            throw;
        }
    }
}
