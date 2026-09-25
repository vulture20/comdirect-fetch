using System.Text.Json;
using System.Threading;
using ComdirectFetch.Api;
using ComdirectFetch.Data;
using ComdirectFetch.Domain;
using Microsoft.Extensions.Options;

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
/// Der Zustand lebt primär im Prozessspeicher; ist Comdirect__TokenEncryptionKeyBase64
/// gesetzt, wird der aktuelle Token zusätzlich AES-256-GCM-verschlüsselt in
/// auth_token_store abgelegt, sodass <see cref="TryRestoreAsync"/> ihn nach einem Neustart
/// wiederherstellen kann, solange er (ggf. per Refresh) noch gültig ist - ohne neue
/// TAN-Freigabe. Ohne gesetzten Schlüssel bleibt es beim bisherigen Verhalten (jeder
/// Neustart braucht eine neue TAN-Freigabe).
/// </summary>
public sealed class ComdirectAuthCoordinator(
    ComdirectAuthClient authClient,
    AuthTokenRepository tokenRepository,
    IOptions<ComdirectApiOptions> options,
    ILogger<ComdirectAuthCoordinator> logger)
{
    private readonly Lock _lock = new();
    private readonly byte[]? _encryptionKey = DecodeKeyOrNull(options.Value.TokenEncryptionKeyBase64, logger);
    private OAuthToken? _initialToken;
    private SessionInfo? _session;
    private TanChallenge? _pendingChallenge;

    private static byte[]? DecodeKeyOrNull(string? base64Key, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(base64Key))
        {
            return null;
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64Key);
        }
        catch (FormatException)
        {
            logger.LogWarning(
                "Comdirect__TokenEncryptionKeyBase64 ist kein gültiger Base64-Wert – Token-Persistierung bleibt deaktiviert.");
            return null;
        }

        if (key.Length != SecretEncryption.KeySizeBytes)
        {
            logger.LogWarning(
                "Comdirect__TokenEncryptionKeyBase64 muss {Expected} Bytes dekodieren (AES-256), hat aber {Actual} – Token-Persistierung bleibt deaktiviert.",
                SecretEncryption.KeySizeBytes, key.Length);
            return null;
        }

        return key;
    }

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
        await PersistCurrentTokenAsync(cancellationToken);
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

            await PersistCurrentTokenAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Token-Refresh fehlgeschlagen – Session vermutlich abgelaufen. Neue TAN-Freigabe erforderlich.");
            lock (_lock)
            {
                CurrentToken = null;
                State = AuthState.NichtAuthentifiziert;
            }

            await tokenRepository.ClearAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Versucht beim Start, eine vorher persistierte Session wiederherzustellen, ohne eine
    /// neue TAN-Freigabe anzufordern. Gibt true zurück, wenn die Session danach gültig und
    /// aktiv ist. Ohne konfigurierten Schlüssel oder ohne gespeicherten Token (oder wenn der
    /// gespeicherte Token sich nicht mehr erneuern lässt) wird false zurückgegeben - der
    /// Dienst verhält sich dann wie bisher (NichtAuthentifiziert, /auth/start nötig).
    /// </summary>
    public async Task<bool> TryRestoreAsync(CancellationToken cancellationToken = default)
    {
        if (_encryptionKey is null)
        {
            return false;
        }

        var stored = await tokenRepository.LoadAsync(cancellationToken);
        if (stored is null)
        {
            return false;
        }

        OAuthToken token;
        try
        {
            var plaintext = SecretEncryption.Decrypt(_encryptionKey, new EncryptedSecret(stored.Nonce, stored.Ciphertext, stored.Tag));
            token = JsonSerializer.Deserialize<OAuthToken>(plaintext)
                ?? throw new InvalidOperationException("Persistierter Token konnte nicht deserialisiert werden.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Persistierter comdirect-Token konnte nicht entschlüsselt werden – neue TAN-Freigabe erforderlich.");
            await tokenRepository.ClearAsync(cancellationToken);
            return false;
        }

        lock (_lock)
        {
            CurrentToken = token;
        }

        try
        {
            await RefreshAsync(cancellationToken);
        }
        catch (Exception)
        {
            // RefreshAsync hat CurrentToken/State/Store bereits bereinigt und den Fehler geloggt.
            return false;
        }

        lock (_lock)
        {
            State = AuthState.Authentifiziert;
        }

        logger.LogInformation("comdirect-Session nach Neustart wiederhergestellt, ohne neue TAN-Freigabe.");
        return true;
    }

    private async Task PersistCurrentTokenAsync(CancellationToken cancellationToken)
    {
        if (_encryptionKey is null)
        {
            return;
        }

        OAuthToken? current;
        lock (_lock)
        {
            current = CurrentToken;
        }

        if (current is null)
        {
            return;
        }

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(current);
        var encrypted = SecretEncryption.Encrypt(_encryptionKey, plaintext);
        await tokenRepository.SaveAsync(encrypted.Nonce, encrypted.Ciphertext, encrypted.Tag, cancellationToken);
    }
}
