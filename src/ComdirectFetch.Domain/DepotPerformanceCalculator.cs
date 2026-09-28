namespace ComdirectFetch.Domain;

/// <summary>Ein Depotwert an einem bestimmten Tag (letzter Snapshot des Tages, siehe <see cref="DepotPerformanceCalculator.ConsolidateToDailyLastValue"/>).</summary>
public sealed record DailyDepotValuation(DateOnly Date, decimal TotalValue);

/// <summary>Ergebnis der Netto-Kapitaleinsatz-Methode (KONZEPT.md Abschnitt 6, Issue #12).</summary>
public sealed record NetInvestedCapitalResult(
    decimal NetInvestedCapital,
    decimal DividendsReceived,
    decimal CurrentValue,
    decimal ProfitLoss,
    decimal? ReturnPercent);

/// <summary>Ergebnis der tagesverketteten Time-Weighted-Return-Berechnung.</summary>
public sealed record TimeWeightedReturnResult(decimal ReturnPercent, int DaysConsidered);

/// <summary>
/// Berechnet die um externe Ein-/Auszahlungen bereinigte Depot-Performance in zwei Varianten
/// (Issue #12, KONZEPT.md Abschnitt 6 Phase 4): eine einfache Netto-Kapitaleinsatz-Kennzahl und
/// eine präzisere, tagesverkettete Time-Weighted Return (TWR). Beide reine, DB-unabhängige
/// Funktionen (gleiches Muster wie <see cref="CategorizationLogic"/>) - Ein-/Ausgabe sind
/// bereits geladene Domain-Objekte, keine eigene DB-Abfrage hier.
/// </summary>
public static class DepotPerformanceCalculator
{
    /// <summary>
    /// Aktueller Depotwert + erhaltene Dividenden − Netto-Kapitaleinsatz. Einfach verständlich,
    /// bewusst ohne Zeitgewichtung: eine späte, große Einzahlung wird genauso behandelt wie eine
    /// frühe - für eine präzisere Betrachtung siehe <see cref="CalculateTimeWeightedReturn"/>.
    /// </summary>
    public static NetInvestedCapitalResult CalculateNetInvestedCapital(
        decimal currentDepotValue,
        IReadOnlyCollection<Transaction> settlementAccountTransactions)
    {
        var netInvested = 0m;
        var dividends = 0m;

        foreach (var transaction in settlementAccountTransactions)
        {
            switch (DepotCashflowClassifier.Classify(transaction))
            {
                case DepotCashflowKind.Dividend:
                    dividends += transaction.Amount;
                    break;
                case DepotCashflowKind.ExternalDeposit:
                    netInvested -= transaction.Amount; // Amount ist negativ bei einer Einzahlung/einem Kauf
                    break;
                case DepotCashflowKind.ExternalWithdrawal:
                    netInvested -= transaction.Amount; // Amount ist positiv bei einer Auszahlung -> mindert den Einsatz
                    break;
            }
        }

        var profitLoss = currentDepotValue + dividends - netInvested;
        var returnPercent = netInvested > 0 ? profitLoss / netInvested * 100m : (decimal?)null;
        return new NetInvestedCapitalResult(netInvested, dividends, currentDepotValue, profitLoss, returnPercent);
    }

    /// <summary>
    /// Reduziert eine zeitlich aufsteigend sortierte Liste von Depotwert-Zeitpunkten auf einen
    /// Wert pro Kalendertag (der jeweils letzte Wert des Tages) - gleiches Konsolidierungsprinzip
    /// wie <c>RetentionRepository</c> für account_balances/portfolio_snapshots (KONZEPT.md
    /// Abschnitt 11), hier nur als reine Funktion statt als DB-Operation, weil der Cashflow-
    /// gewichtete Tagesertrag pro Kalendertag berechnet wird, nicht pro einzelnem Snapshot.
    /// </summary>
    public static IReadOnlyList<DailyDepotValuation> ConsolidateToDailyLastValue(
        IReadOnlyList<(DateTimeOffset Timestamp, decimal TotalValue)> snapshotsAscending)
    {
        var byDay = new List<DailyDepotValuation>();
        foreach (var (timestamp, totalValue) in snapshotsAscending)
        {
            var date = DateOnly.FromDateTime(timestamp.UtcDateTime);
            if (byDay.Count > 0 && byDay[^1].Date == date)
            {
                byDay[^1] = new DailyDepotValuation(date, totalValue);
            }
            else
            {
                byDay.Add(new DailyDepotValuation(date, totalValue));
            }
        }

        return byDay;
    }

    /// <summary>
    /// Tagesverkettete Time-Weighted Return: für jeden Tagesübergang wird die Rendite um die in
    /// diesem Zeitraum aufgetretenen externen Kapitalbewegungen bereinigt (Dividenden zählen wie
    /// eine "Einzahlung ohne Kapitaleinsatz", da sie den Depotwert erhöhen, ohne dass der Nutzer
    /// selbst Geld eingesetzt hat), dann werden die Tagesrenditen geometrisch verkettet
    /// (Π(1+r_Tag) − 1). Erfordert mindestens zwei Tageswerte; ein Tag mit Startwert 0 wird
    /// übersprungen (Division durch 0 nicht sinnvoll definierbar). Rückgabe null, wenn nach dem
    /// Filtern kein einziger Tagesübergang übrig bleibt.
    /// </summary>
    public static TimeWeightedReturnResult? CalculateTimeWeightedReturn(
        IReadOnlyList<DailyDepotValuation> dailyValuationsAscending,
        IReadOnlyCollection<Transaction> settlementAccountTransactions)
    {
        if (dailyValuationsAscending.Count < 2)
        {
            return null;
        }

        var flowByDate = new Dictionary<DateOnly, decimal>();
        foreach (var transaction in settlementAccountTransactions)
        {
            // Dividende und Einzahlung/Kauf zählen als positiver Beitrag (negatives Amount bei
            // einer Einzahlung/einem Kauf), eine Auszahlung/ein Verkauf als negativer - kontofremde
            // Umsätze (DepotCashflowKind.Other, z. B. Alltagsausgaben auf dem Girokonto) fließen
            // bewusst nicht ein, siehe DepotCashflowKind.Other.
            var contribution = DepotCashflowClassifier.Classify(transaction) switch
            {
                DepotCashflowKind.Dividend => transaction.Amount,
                DepotCashflowKind.ExternalDeposit => -transaction.Amount,
                DepotCashflowKind.ExternalWithdrawal => -transaction.Amount,
                _ => 0m,
            };
            if (contribution != 0m)
            {
                flowByDate[transaction.BookingDate] = flowByDate.GetValueOrDefault(transaction.BookingDate) + contribution;
            }
        }

        var chainedGrowthFactor = 1m;
        var daysConsidered = 0;

        for (var i = 1; i < dailyValuationsAscending.Count; i++)
        {
            var previous = dailyValuationsAscending[i - 1];
            var current = dailyValuationsAscending[i];
            if (previous.TotalValue <= 0)
            {
                continue;
            }

            var periodFlow = flowByDate
                .Where(kvp => kvp.Key > previous.Date && kvp.Key <= current.Date)
                .Sum(kvp => kvp.Value);

            var dailyReturn = (current.TotalValue - previous.TotalValue - periodFlow) / previous.TotalValue;
            chainedGrowthFactor *= 1 + dailyReturn;
            daysConsidered++;
        }

        return daysConsidered > 0
            ? new TimeWeightedReturnResult((chainedGrowthFactor - 1) * 100m, daysConsidered)
            : null;
    }
}
