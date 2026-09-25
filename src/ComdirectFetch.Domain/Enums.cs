namespace ComdirectFetch.Domain;

/// <summary>Art einer Kategorie für Kontoumsätze (KONZEPT.md Abschnitt 6).</summary>
public enum CategoryType
{
    Einnahme,
    Ausgabe,
    InternNeutral
}

/// <summary>Welche Datenart ein sync_log-Eintrag betrifft (KONZEPT.md Abschnitt 3/5).</summary>
public enum SyncDataKind
{
    TokenRefresh,
    Salden,
    Depotuebersicht,
    Kontoumsaetze,
    Konsolidierung
}

/// <summary>Ergebnis eines Abrufvorgangs, inkl. des TAN-Freigabe-Sonderfalls aus Abschnitt 3.</summary>
public enum SyncStatus
{
    Erfolgreich,
    Fehlgeschlagen,
    FreigabeErforderlich
}
