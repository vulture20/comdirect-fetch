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
    public required string Username { get; set; }
    public required string Password { get; set; }

    /// <summary>
    /// Base64-kodierter 32-Byte-AES-256-Schlüssel zur Verschlüsselung des persistierten
    /// Session-Tokens (KONZEPT.md Abschnitt 3/9, "Auth-Status über Neustarts persistieren").
    /// Optional: ist der Wert nicht gesetzt, bleibt die Persistierung deaktiviert und jeder
    /// Neustart verlangt wie bisher eine neue TAN-Freigabe. Niemals in der DB ablegen.
    /// </summary>
    public string? TokenEncryptionKeyBase64 { get; set; }
}
