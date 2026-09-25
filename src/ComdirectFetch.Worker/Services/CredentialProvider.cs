using System.Text.Json;
using ComdirectFetch.Api;
using ComdirectFetch.Data;
using ComdirectFetch.Domain;
using Microsoft.Extensions.Options;

namespace ComdirectFetch.Worker.Services;

/// <summary>
/// Liefert die comdirect-Login-Zugangsdaten (Zugangsnummer/PIN) zur Laufzeit statt beim
/// DI-Container-Bau (KONZEPT.md Abschnitt 10 B, "Sichere Ablage der comdirect-Zugangsdaten"):
/// bevorzugt einen verschlüsselt in credential_store abgelegten Wert, fällt sonst auf
/// ComdirectApiOptions.Username/Password zurück. Der Bootstrap-Endpunkt
/// (POST /admin/credentials, siehe Program.cs) ruft <see cref="SetCredentialsAsync"/> auf, um
/// Zugangsnummer/PIN einmalig verschlüsselt abzulegen; danach kann .env geleert werden. Der
/// dazu genutzte Schlüssel ist dediziert (eigene Datei, Comdirect__CredentialKeyFilePath) und
/// unabhängig vom Session-Token-Schlüssel (Comdirect__TokenEncryptionKeyBase64) - andere
/// Geheimnisse, andere Schlüssel.
/// </summary>
public sealed class CredentialProvider(
    CredentialRepository credentialRepository,
    IOptions<ComdirectApiOptions> options,
    ILogger<CredentialProvider> logger) : ICredentialProvider
{
    private readonly ComdirectApiOptions _options = options.Value;
    private readonly Lazy<byte[]?> _key = new(() => LoadKeyOrNull(options.Value.CredentialKeyFilePath, logger));

    public async Task<(string Username, string Password)> GetLoginCredentialsAsync(CancellationToken cancellationToken = default)
    {
        var stored = await TryLoadStoredAsync(cancellationToken);
        if (stored is not null)
        {
            return stored.Value;
        }

        if (string.IsNullOrEmpty(_options.Username) || string.IsNullOrEmpty(_options.Password))
        {
            throw new InvalidOperationException(
                "Keine comdirect-Zugangsdaten verfügbar: weder in credential_store hinterlegt noch über " +
                "Comdirect__Username/Comdirect__Password konfiguriert.");
        }

        return (_options.Username, _options.Password);
    }

    /// <summary>
    /// Verschlüsselt Zugangsnummer/PIN mit dem dedizierten Schlüssel und legt sie in
    /// credential_store ab (Bootstrap-Schritt, POST /admin/credentials). Verifiziert das
    /// Ergebnis per Round-Trip-Entschlüsselung, bevor sie als erfolgreich gilt, damit kein
    /// Zugriff stillschweigend verloren geht.
    /// </summary>
    public async Task SetCredentialsAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        var key = _key.Value
            ?? throw new InvalidOperationException(
                $"Kein Verschlüsselungsschlüssel unter '{_options.CredentialKeyFilePath}' gefunden " +
                "(Comdirect__CredentialKeyFilePath). Ohne Schlüssel kann POST /admin/credentials nichts " +
                "verschlüsselt ablegen.");

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new StoredLoginCredentials(username, password));
        var encrypted = SecretEncryption.Encrypt(key, plaintext);
        await credentialRepository.SaveAsync(encrypted.Nonce, encrypted.Ciphertext, encrypted.Tag, cancellationToken);

        var verified = await TryLoadStoredAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "Verifikation fehlgeschlagen: gespeicherte Zugangsdaten konnten nicht zurückgelesen werden.");
        if (verified.Username != username || verified.Password != password)
        {
            throw new InvalidOperationException(
                "Verifikation fehlgeschlagen: entschlüsselter Wert weicht vom eingegebenen Wert ab.");
        }

        logger.LogInformation("comdirect-Zugangsnummer/PIN verschlüsselt in credential_store abgelegt.");
    }

    private async Task<(string Username, string Password)?> TryLoadStoredAsync(CancellationToken cancellationToken)
    {
        var key = _key.Value;
        if (key is null)
        {
            return null;
        }

        var stored = await credentialRepository.LoadAsync(cancellationToken);
        if (stored is null)
        {
            return null;
        }

        try
        {
            var plaintext = SecretEncryption.Decrypt(key, new EncryptedSecret(stored.Nonce, stored.Ciphertext, stored.Tag));
            var credentials = JsonSerializer.Deserialize<StoredLoginCredentials>(plaintext)
                ?? throw new InvalidOperationException("Gespeicherte Zugangsdaten konnten nicht deserialisiert werden.");
            return (credentials.Username, credentials.Password);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Gespeicherte comdirect-Zugangsdaten konnten nicht entschlüsselt werden - falle auf Konfiguration zurück.");
            return null;
        }
    }

    private static byte[]? LoadKeyOrNull(string path, ILogger logger)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] key;
        try
        {
            key = File.ReadAllBytes(path);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Schlüsseldatei '{Path}' konnte nicht gelesen werden - Bootstrap-Mechanismus bleibt inaktiv.", path);
            return null;
        }

        if (key.Length != SecretEncryption.KeySizeBytes)
        {
            logger.LogWarning(
                "Schlüsseldatei '{Path}' enthält {Actual} Bytes, erwartet werden {Expected} (AES-256) - Bootstrap-Mechanismus bleibt inaktiv.",
                path, key.Length, SecretEncryption.KeySizeBytes);
            return null;
        }

        return key;
    }
}

internal sealed record StoredLoginCredentials(string Username, string Password);
