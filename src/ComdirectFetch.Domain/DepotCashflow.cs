namespace ComdirectFetch.Domain;

/// <summary>
/// Wie ein Umsatz auf einem depot-verknüpften Konto für die Kapitaleinsatz-/Rendite-Berechnung zu
/// werten ist (KONZEPT.md Abschnitt 13, Issue #12). Modell: Depotwert = Positionen + Guthaben des
/// Standard-Verrechnungskontos; nur Geld, das die Depot-Grenze von außen überschreitet, ist eine
/// Kapitalbewegung.
/// </summary>
public enum DepotCashflowKind
{
    /// <summary>Dividenden-/Zinsgutschrift auf dem Standard-Verrechnungskonto - Anlageertrag. Steckt bereits
    /// im Guthaben (und damit im Depotwert), ist also keine Kapitalbewegung.</summary>
    Dividend,

    /// <summary>Geld fließt von außerhalb in den Depot-Bereich: ein Sparplan-Kauf, der direkt von einem
    /// allgemeinen Konto (z. B. Girokonto) abgebucht wird, oder eine Überweisung auf das Verrechnungskonto.</summary>
    ExternalDeposit,

    /// <summary>Geld verlässt den Depot-Bereich: ein Verkaufserlös oder eine Ertragsgutschrift, die auf einem
    /// allgemeinen Konto landet, oder eine Überweisung vom Verrechnungskonto weg.</summary>
    ExternalWithdrawal,

    /// <summary>Kauf/Verkauf über das Standard-Verrechnungskonto selbst: Guthaben wird zu Wertpapieren oder
    /// umgekehrt, die Depot-Grenze wird nicht überschritten (live bestätigt: ein Kauf über 3.089 € wurde
    /// komplett aus vorhandenem Guthaben bezahlt, ohne jede Überweisung). Keine Kapitalbewegung.</summary>
    InternalTrade,

    /// <summary>Alles andere - wird ignoriert. Ein verknüpftes Konto wie das Girokonto wickelt neben
    /// Sparplan-Käufen auch normale Alltagsausgaben ab; nur die oben genannten Typen sind eindeutig
    /// depot-bezogen (ohne diese Einschränkung flossen live Lebensmitteleinkäufe und Bargeldabhebungen
    /// in die Berechnung ein). Auf dem Verrechnungskonto werden bewusst nur "Transfer"-Buchungen als
    /// externe Bewegung gewertet; dafür liegt noch kein echter Umsatz zum Verifizieren vor.</summary>
    Other,
}

/// <summary>
/// Reine, DB-unabhängige Klassifizierung (siehe CategorizationLogic für dasselbe Muster). Entscheidend
/// sind transaction_type und ob es sich um das Standard-Verrechnungskonto handelt - dieselbe
/// Buchungsart (`Securities`) ist dort intern, auf einem allgemeinen Konto dagegen extern.
/// </summary>
public static class DepotCashflowClassifier
{
    public static DepotCashflowKind Classify(Transaction transaction, bool isDefaultSettlementAccount) =>
        transaction.TransactionType switch
        {
            "Interest / Dividends" => isDefaultSettlementAccount
                ? DepotCashflowKind.Dividend
                : DepotCashflowKind.ExternalWithdrawal,
            "Securities" => isDefaultSettlementAccount
                ? DepotCashflowKind.InternalTrade
                : transaction.Amount < 0 ? DepotCashflowKind.ExternalDeposit : DepotCashflowKind.ExternalWithdrawal,
            "Transfer" when isDefaultSettlementAccount =>
                transaction.Amount >= 0 ? DepotCashflowKind.ExternalDeposit : DepotCashflowKind.ExternalWithdrawal,
            _ => DepotCashflowKind.Other,
        };
}
