namespace ComdirectFetch.Api;

/// <summary>
/// Zugangsdaten und Basis-URL für die comdirect REST API (KONZEPT.md Abschnitt 4, Kategorie
/// "comdirect-Zugangsdaten (Nutzer-Ebene)"). Wird über Umgebungsvariablen befüllt.
/// </summary>
public sealed class ComdirectApiOptions
{
    public const string SectionName = "Comdirect";

    public string BaseUrl { get; set; } = "https://api.comdirect.de";
    public required string ClientId { get; set; }
    public required string ClientSecret { get; set; }

    /// <summary>
    /// Zugangsnummer/PIN. Optional geworden (KONZEPT.md Abschnitt 10, "Sichere Ablage der
    /// comdirect-Zugangsdaten"): sind sie über POST /admin/credentials im verschlüsselten
    /// credential_store hinterlegt, hat dieser Vorrang und diese Felder bleiben leer/werden
    /// nach dem Bootstrap aus .env entfernt. Nur als Fallback für Deployments ohne
    /// Bootstrap-Schritt weiterhin unterstützt.
    /// </summary>
    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>
    /// Base64-kodierter 32-Byte-AES-256-Schlüssel zur Verschlüsselung des persistierten
    /// Session-Tokens (KONZEPT.md Abschnitt 3/9, "Auth-Status über Neustarts persistieren").
    /// Optional: ist der Wert nicht gesetzt, bleibt die Persistierung deaktiviert und jeder
    /// Neustart verlangt wie bisher eine neue TAN-Freigabe. Niemals in der DB ablegen.
    /// </summary>
    public string? TokenEncryptionKeyBase64 { get; set; }

    /// <summary>
    /// Pfad zu einer Datei mit den rohen Bytes eines dedizierten 32-Byte-AES-256-Schlüssels
    /// zur Verschlüsselung von Zugangsnummer/PIN in credential_store (KONZEPT.md Abschnitt 10
    /// B) - bewusst ein eigener Schlüssel, nicht TokenEncryptionKeyBase64, und bewusst eine
    /// Datei statt einer Env-Var, damit er unabhängig von .env aufbewahrt werden kann. Optional:
    /// existiert die Datei unter diesem Pfad nicht, bleibt der Bootstrap-Mechanismus inaktiv und
    /// Username/Password werden weiterhin aus der Konfiguration gelesen.
    /// </summary>
    public string CredentialKeyFilePath { get; set; } = "/run/secrets/credential_key";
}
