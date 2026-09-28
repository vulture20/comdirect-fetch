namespace ComdirectFetch.Domain;

/// <summary>
/// Wie ein Umsatz auf einem depot-verknüpften Verrechnungskonto für die Kapitaleinsatz-/
/// Rendite-Berechnung zu werten ist (KONZEPT.md Abschnitt 6, Phase 4 / Issue #12).
/// </summary>
public enum DepotCashflowKind
{
    /// <summary>Dividenden-/Zinsgutschrift (comdirect: transaction_type "Interest / Dividends") - Anlageertrag,
    /// zählt NICHT als vom Nutzer eingesetztes Kapital, erhöht aber die Rendite.</summary>
    Dividend,

    /// <summary>Wertpapier-Kauf (comdirect: transaction_type "Securities", negativer Betrag) - Geld fließt
    /// von außerhalb in den Depot-Bereich, zählt als vom Nutzer eingesetztes Kapital.</summary>
    ExternalDeposit,

    /// <summary>Wertpapier-Verkauf (comdirect: transaction_type "Securities", positiver Betrag) - Geld
    /// verlässt den Depot-Bereich, mindert das eingesetzte Kapital.</summary>
    ExternalWithdrawal,

    /// <summary>Jeder andere Umsatz auf einem verknüpften Konto - wird ignoriert. Wichtig, weil ein
    /// verknüpftes Konto (comdirect liefert es als settlementAccountId) nicht zwingend
    /// ausschließlich für das Depot genutzt wird: das Girokonto z. B. wickelt sowohl
    /// Sparplan-Käufe als auch normale Alltagsausgaben (Lebensmittel, Kartenzahlungen, Bargeld
    /// ...) ab. Nur "Securities"/"Interest / Dividends" sind eindeutig depot-bezogen; alles
    /// andere ließe sich nicht zuverlässig von normaler Kontonutzung unterscheiden und würde die
    /// Kapitaleinsatz-Berechnung sonst massiv verfälschen (live mit echten Daten bestätigt:
    /// ohne diese Einschränkung flossen u. a. Lebensmitteleinkäufe und Bargeldabhebungen mit
    /// ein). Eine echte externe Ein-/Auszahlung auf das dedizierte Verrechnungskonto (z. B. ein
    /// klassischer "Transfer") würde davon ebenfalls ignoriert - dafür fehlen bislang echte
    /// Testdaten, siehe KONZEPT.md Abschnitt 6 Phase 4 / Issue #12.</summary>
    Other,
}

/// <summary>
/// Reine, DB-unabhängige Klassifizierung (siehe CategorizationLogic für dasselbe Muster).
/// Bewusst NUR anhand des transaction_type entschieden (nicht anhand des Kontos), da ein
/// depot-verknüpftes Konto wie das Girokonto auch für kontofremde Zwecke genutzt wird - siehe
/// <see cref="DepotCashflowKind.Other"/>.
/// </summary>
public static class DepotCashflowClassifier
{
    public static DepotCashflowKind Classify(Transaction transaction) => transaction.TransactionType switch
    {
        "Interest / Dividends" => DepotCashflowKind.Dividend,
        "Securities" => transaction.Amount < 0 ? DepotCashflowKind.ExternalDeposit : DepotCashflowKind.ExternalWithdrawal,
        _ => DepotCashflowKind.Other,
    };
}
