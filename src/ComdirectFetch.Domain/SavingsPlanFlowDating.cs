namespace ComdirectFetch.Domain;

/// <summary>
/// Ab wann zählt ein Sparplan-Kauf als externe Einzahlung ins Depot (Issue #18, KONZEPT.md Abschnitt 13)?
/// Live belegt: die Kontobuchung trägt den Ausführungstag, die neuen Anteile erscheinen aber erst am
/// Depot-Buchungstag (Folgetag) in den Positionen, die Valuta liegt noch später. Damit Gesamtwert und Kapital am
/// selben Tag steigen, wird jede Wertpapier-Kontobuchung ihrer Depot-Transaktion zugeordnet und deren
/// Buchungstag verwendet. Reine, DB-unabhängige Funktion.
///
/// Zuordnung (live für alle Trades eindeutig): WKN oder ISIN im Buchungstext, Richtung passend zum Vorzeichen
/// (Belastung = Zugang im Depot), Betrag gleich dem Abrechnungsbetrag der Depot-Transaktion, Handelstag
/// höchstens <see cref="MaxDaysApart"/> Tage vom Konto-Buchungstag entfernt. Jede Depot-Transaktion wird
/// höchstens einmal vergeben. Ohne eindeutigen Treffer gibt es keinen Eintrag (der Aufrufer bleibt beim
/// Konto-Buchungstag).
/// </summary>
public static class SavingsPlanFlowDating
{
    public const int MaxDaysApart = 5;

    /// <returns>Konto-Umsatz-Id → Depot-Buchungstag.</returns>
    public static IReadOnlyDictionary<long, DateOnly> Resolve(
        IEnumerable<Transaction> accountTransactions,
        IReadOnlyCollection<DepotTransaction> depotTransactions)
    {
        var result = new Dictionary<long, DateOnly>();
        var used = new HashSet<string>();

        foreach (var transaction in accountTransactions.OrderBy(t => t.BookingDate).ThenBy(t => t.Id))
        {
            var direction = transaction.Amount < 0 ? "IN" : "OUT";
            var text = transaction.BookingText ?? "";
            var candidates = depotTransactions
                .Where(d => !used.Contains(d.ComdirectTransactionId)
                    && d.BookingDate is not null
                    && d.BusinessDate is { } business
                    && Math.Abs(business.DayNumber - transaction.BookingDate.DayNumber) <= MaxDaysApart
                    && d.Direction == direction
                    && d.TransactionValue == Math.Abs(transaction.Amount)
                    && ((d.Wkn is { Length: > 0 } wkn && text.Contains(wkn, StringComparison.OrdinalIgnoreCase))
                        || (d.Isin is { Length: > 0 } isin && text.Contains(isin, StringComparison.OrdinalIgnoreCase))))
                .GroupBy(d => Math.Abs(d.BusinessDate!.Value.DayNumber - transaction.BookingDate.DayNumber))
                .OrderBy(g => g.Key)
                .FirstOrDefault()
                ?.ToList();

            // Nur ein eindeutig nächstgelegener Treffer zählt - bei Gleichstand lieber nichts als raten.
            if (candidates is [var match])
            {
                used.Add(match.ComdirectTransactionId);
                result[transaction.Id] = match.BookingDate!.Value;
            }
        }

        return result;
    }
}
