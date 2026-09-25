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
}
