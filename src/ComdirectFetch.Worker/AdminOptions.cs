namespace ComdirectFetch.Worker;

/// <summary>
/// Zugriffsschutz für die neuen /admin/*-Endpunkte (Web-Oberfläche für Kategorien/Regeln,
/// GitHub-Issue #13, KONZEPT.md Abschnitt 12) - bewusst strenger als die übrigen,
/// unauthentifizierten /debug/*-/auth/*-Endpunkte, da hier dauerhafte Konfiguration geändert
/// wird statt nur eine Aktion angestoßen.
/// </summary>
public sealed class AdminOptions
{
    public const string SectionName = "Admin";

    /// <summary>Ohne gesetztes Passwort bleibt /admin/* komplett deaktiviert (503), nicht ungeschützt erreichbar.</summary>
    public string? Password { get; set; }
}
