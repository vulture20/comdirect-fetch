namespace ComdirectFetch.Domain;

/// <summary>Ein Depot-Snapshot als Eingabe: Zeitpunkt und der reine Positionswert laut comdirect (ohne Guthaben).</summary>
public sealed record DepotSnapshotInput(long SnapshotId, DateTimeOffset Timestamp, decimal PositionsValue);

/// <summary>Ein Umsatz auf einem depot-verknüpften Konto, ergänzt um die Info, ob es das Standard-Verrechnungskonto ist.</summary>
public sealed record LinkedTransaction(Transaction Transaction, bool IsDefaultSettlementAccount);

/// <summary>Die für einen Snapshot berechneten Kennzahlen (KONZEPT.md Abschnitt 13, Issue #12).</summary>
public sealed record DepotPerformancePoint(
    long SnapshotId,
    DateTimeOffset Timestamp,
    decimal SettlementCash,
    decimal NetInvestedCapital,
    decimal DividendsReceived,
    decimal? TimeWeightedReturnPercent);

/// <summary>
/// Berechnet die um externe Ein-/Auszahlungen bereinigte Depot-Performance (Issue #12,
/// KONZEPT.md Abschnitt 13). Reine, DB-unabhängige Funktion (gleiches Muster wie
/// <see cref="CategorizationLogic"/>).
///
/// Modell: Gesamtwert = Positionen (comdirect) + Guthaben des Standard-Verrechnungskontos. Eine
/// Kapitalbewegung ist nur Geld, das die Depot-Grenze von außen überschreitet
/// (<see cref="DepotCashflowClassifier"/>). Bezugspunkt ist der erste Snapshot: sein Gesamtwert gilt als
/// Startkapital, danach kommen nur noch externe Bewegungen hinzu. Dadurch ist der Gewinn auch bei einem
/// Depot mit erheblichem Wert vor Trackingbeginn aussagekräftig (frühere Fassung: Kapitaleinsatz nur aus
/// den getrackten Käufen, live ca. 4090 % Rendite bei einem Depot von ca. 37.000 €).
///
/// Das Guthaben je Tag wird aus dem aktuellen Saldo und den Buchungen zurückgerechnet (Saldo minus alle
/// Umsätze nach dem Tag) statt aus einzelnen Saldo-Stichproben zu lesen. Grund, live belegt: comdirect
/// zeigt eine ausgeführte Order sofort in den Depot-Positionen (Munich Re: 28.09. morgens), der gebuchte
/// Saldo sinkt aber erst rund einen Tag später (29.09. nachts) und die Valuta liegt nochmal später
/// (30.09.). Mit dem Buchungstag als Bezug (= Ausführungstag) stimmen Positionen und Guthaben zusammen.
/// Bewegungen werden deshalb ebenfalls zum Buchungstag angesetzt, nicht zur Valuta.
/// </summary>
public static class DepotPerformanceCalculator
{
    /// <param name="snapshotsAscending">Alle Snapshots des Depots, zeitlich aufsteigend; der erste ist der Bezugspunkt.</param>
    /// <param name="latestBalanceByDefaultAccount">Jeweils aktuellster gebuchter Saldo der Standard-Verrechnungskonten (Ankerpunkt der Rückrechnung).</param>
    /// <param name="transactions">Alle Umsätze der verknüpften Konten (vollständig - bei den Standard-Verrechnungskonten alle Buchungsarten, sonst stimmt die Rückrechnung nicht).</param>
    /// <returns>Ein Eintrag je Snapshot; leer, wenn kein Standard-Verrechnungskonto bzw. kein Saldo bekannt ist.</returns>
    public static IReadOnlyList<DepotPerformancePoint> Calculate(
        IReadOnlyList<DepotSnapshotInput> snapshotsAscending,
        IReadOnlyDictionary<long, decimal> latestBalanceByDefaultAccount,
        IReadOnlyCollection<LinkedTransaction> transactions)
    {
        if (latestBalanceByDefaultAccount.Count == 0 || snapshotsAscending.Count == 0)
        {
            return [];
        }

        var latestBalance = latestBalanceByDefaultAccount.Values.Sum();
        var ledger = new List<(DateOnly Date, decimal Amount)>();
        var flows = new List<(DateOnly Date, decimal Amount)>();
        var dividends = new List<(DateOnly Date, decimal Amount)>();
        foreach (var linked in transactions)
        {
            var transaction = linked.Transaction;
            var date = transaction.BookingDate;
            if (linked.IsDefaultSettlementAccount)
            {
                ledger.Add((date, transaction.Amount));
            }

            switch (DepotCashflowClassifier.Classify(transaction, linked.IsDefaultSettlementAccount))
            {
                case DepotCashflowKind.ExternalDeposit:
                    flows.Add((date, Math.Abs(transaction.Amount)));
                    break;
                case DepotCashflowKind.ExternalWithdrawal:
                    flows.Add((date, -Math.Abs(transaction.Amount)));
                    break;
                case DepotCashflowKind.Dividend:
                    dividends.Add((date, transaction.Amount));
                    break;
            }
        }

        var ledgerSums = new DateCumulative(ledger);
        var flowSums = new DateCumulative(flows);
        var dividendSums = new DateCumulative(dividends);
        var ledgerTotal = ledger.Sum(e => e.Amount);

        var valid = snapshotsAscending
            .Select(snapshot =>
            {
                var date = DateOnly.FromDateTime(snapshot.Timestamp.UtcDateTime);
                // Saldo nach allen bis einschließlich diesem Tag gebuchten Umsätzen.
                var cash = latestBalance - (ledgerTotal - ledgerSums.SumBetween(DateOnly.MinValue, date));
                return (Snapshot: snapshot, Date: date, Cash: cash, Value: snapshot.PositionsValue + cash);
            })
            .ToList();

        var baselineValue = valid[0].Value;
        var baselineDate = valid[0].Date;

        var points = new List<DepotPerformancePoint>(valid.Count);
        var chainedFactor = 1m;

        // Der Bezugspunkt selbst ist der Startwert der Kette: auch die Kursbewegung im weiteren Verlauf des
        // Bezugstags fließt so in die TWR ein (sonst wären TWR und Gewinn ab dem ersten Tag uneinheitlich).
        DateOnly? previousDate = baselineDate;
        var previousValue = baselineValue;

        var i = 0;
        while (i < valid.Count)
        {
            var day = valid[i].Date;
            decimal? lastDailyReturn = null;
            var j = i;
            while (j < valid.Count && valid[j].Date == day)
            {
                var entry = valid[j];
                decimal? twr = null;
                decimal? dailyReturn = null;
                if (previousDate is { } prior && previousValue > 0)
                {
                    var flow = flowSums.SumBetween(prior, day);
                    dailyReturn = (entry.Value - previousValue - flow) / previousValue;
                    twr = (chainedFactor * (1 + dailyReturn.Value) - 1) * 100m;
                }

                points.Add(new DepotPerformancePoint(
                    entry.Snapshot.SnapshotId,
                    entry.Snapshot.Timestamp,
                    entry.Cash,
                    baselineValue + flowSums.SumBetween(baselineDate, day),
                    dividendSums.SumBetween(baselineDate, day),
                    twr));
                lastDailyReturn = dailyReturn;
                j++;
            }

            // Der letzte Snapshot eines Tages zählt als Tageswert und wird in die Kette übernommen.
            if (lastDailyReturn is { } dayReturn)
            {
                chainedFactor *= 1 + dayReturn;
            }

            previousDate = day;
            previousValue = valid[j - 1].Value;
            i = j;
        }

        return points;
    }

    /// <summary>Kumulierte Summen je Kalendertag, für Summen über einen Datumsbereich in O(log n).</summary>
    private sealed class DateCumulative
    {
        private readonly DateOnly[] _dates;
        private readonly decimal[] _cumulative;

        public DateCumulative(IEnumerable<(DateOnly Date, decimal Amount)> entries)
        {
            var sorted = entries.OrderBy(e => e.Date).ToList();
            _dates = sorted.Select(e => e.Date).ToArray();
            _cumulative = new decimal[sorted.Count];
            var running = 0m;
            for (var i = 0; i < sorted.Count; i++)
            {
                running += sorted[i].Amount;
                _cumulative[i] = running;
            }
        }

        /// <summary>Summe aller Einträge mit Datum nach <paramref name="exclusiveStart"/> bis einschließlich <paramref name="inclusiveEnd"/>.</summary>
        public decimal SumBetween(DateOnly exclusiveStart, DateOnly inclusiveEnd) =>
            SumUpTo(inclusiveEnd) - SumUpTo(exclusiveStart);

        private decimal SumUpTo(DateOnly date)
        {
            var low = 0;
            var high = _dates.Length;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (_dates[middle] <= date)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            return low == 0 ? 0m : _cumulative[low - 1];
        }
    }
}
