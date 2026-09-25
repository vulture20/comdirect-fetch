namespace ComdirectFetch.Worker;

/// <summary>
/// Konfiguration des Konsolidierungs- und Aufräumprozesses (KONZEPT.md Abschnitt 11). Komplett
/// opt-in, analog zu Comdirect__TokenEncryptionKeyBase64/Comdirect__CredentialKeyFilePath: ist
/// ein Zeitraum nicht gesetzt, passiert für den betroffenen Teil nichts - heutiges Verhalten
/// (Daten werden für immer aufbewahrt) bleibt bestehen.
/// </summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    /// <summary>Wie oft der Hintergrunddienst automatisch läuft. Ohne Auswirkung, wenn alle drei Zeiträume unten leer sind.</summary>
    public int IntervalSeconds { get; set; } = 86400;

    /// <summary>
    /// Rohdaten-Frist für account_balances/portfolio_snapshots (Tage). Zeilen, deren Tag älter
    /// ist, werden auf eine Zeile/Tag reduziert (der zeitlich letzte Wert des Tages bleibt
    /// erhalten). Nicht gesetzt: keine Konsolidierung.
    /// </summary>
    public int? RawDataRetentionDays { get; set; }

    /// <summary>
    /// Zusätzliche Frist (Tage), nach der bereits konsolidierte (1 Wert/Tag) Zeilen vollständig
    /// gelöscht werden. Nur wirksam, wenn RawDataRetentionDays ebenfalls gesetzt ist. Nicht
    /// gesetzt: konsolidierte Daten bleiben unbegrenzt erhalten.
    /// </summary>
    public int? ConsolidatedDataRetentionDays { get; set; }

    /// <summary>Frist (Tage) für das Löschen alter sync_log-Zeilen, unabhängig von den beiden Werten oben.</summary>
    public int? SyncLogRetentionDays { get; set; }
}
