using ComdirectFetch.Domain;

namespace ComdirectFetch.Tests;

public class DepotPerformanceCalculatorTests
{
    private const long DefaultAccount = 3;
    private const long GiroAccount = 1;

    private static DateTimeOffset At(int day, int hour = 12) => new(2026, 9, day, hour, 0, 0, TimeSpan.Zero);

    private static Transaction Tx(
        long accountId, decimal amount, string type, int bookingDay, int? valueDay = null, DateTimeOffset? firstSeenAt = null) => new()
    {
        AccountId = accountId,
        ComdirectReference = $"ref-{accountId}-{bookingDay}-{amount}",
        BookingDate = new DateOnly(2026, 9, bookingDay),
        ValueDate = valueDay is { } v ? new DateOnly(2026, 9, v) : null,
        Amount = amount,
        Currency = "EUR",
        TransactionType = type,
        FirstSeenAt = firstSeenAt ?? At(bookingDay, 8),
    };

    private static LinkedTransaction Linked(Transaction transaction) =>
        new(transaction, transaction.AccountId == DefaultAccount);

    private static DepotSnapshotInput Snapshot(long id, DateTimeOffset timestamp, decimal positions) => new(id, timestamp, positions);

    /// <summary>Guthaben des Standard-Verrechnungskontos JETZT (Ankerpunkt); die Umsätze müssen dazu passen (Saldo nach allen Umsätzen).</summary>
    private static IReadOnlyList<DepotPerformancePoint> Calculate(
        IReadOnlyList<DepotSnapshotInput> snapshots, decimal latestBalance, params Transaction[] transactions) =>
        DepotPerformanceCalculator.Calculate(
            snapshots, new Dictionary<long, decimal> { [DefaultAccount] = latestBalance }, transactions.Select(Linked).ToList());

    // ---- Klassifizierung ----

    [Fact]
    public void Dividende_auf_dem_Verrechnungskonto_ist_ein_Ertrag_und_keine_Kapitalbewegung()
    {
        var dividend = Tx(DefaultAccount, 55.59m, "Interest / Dividends", 3);

        Assert.Equal(DepotCashflowKind.Dividend, DepotCashflowClassifier.Classify(dividend, isDefaultSettlementAccount: true));
    }

    [Fact]
    public void Ertrag_auf_einem_allgemeinen_Konto_hat_das_Depot_verlassen()
    {
        var dividend = Tx(GiroAccount, 20m, "Interest / Dividends", 3);

        Assert.Equal(DepotCashflowKind.ExternalWithdrawal, DepotCashflowClassifier.Classify(dividend, isDefaultSettlementAccount: false));
    }

    [Theory]
    [InlineData(-3089.23)]
    [InlineData(341.87)]
    public void Wertpapierhandel_ueber_das_Verrechnungskonto_ist_intern(double amount)
    {
        var trade = Tx(DefaultAccount, (decimal)amount, "Securities", 28, 30);

        Assert.Equal(DepotCashflowKind.InternalTrade, DepotCashflowClassifier.Classify(trade, isDefaultSettlementAccount: true));
    }

    [Fact]
    public void Sparplan_Kauf_vom_Girokonto_ist_eine_externe_Einzahlung_ein_Verkauf_eine_Auszahlung()
    {
        Assert.Equal(DepotCashflowKind.ExternalDeposit,
            DepotCashflowClassifier.Classify(Tx(GiroAccount, -24.97m, "Securities", 24, 25), isDefaultSettlementAccount: false));
        Assert.Equal(DepotCashflowKind.ExternalWithdrawal,
            DepotCashflowClassifier.Classify(Tx(GiroAccount, 500m, "Securities", 24, 25), isDefaultSettlementAccount: false));
    }

    [Fact]
    public void Ueberweisung_auf_dem_Verrechnungskonto_ist_extern_nach_Vorzeichen()
    {
        Assert.Equal(DepotCashflowKind.ExternalDeposit,
            DepotCashflowClassifier.Classify(Tx(DefaultAccount, 1000m, "Transfer", 3), isDefaultSettlementAccount: true));
        Assert.Equal(DepotCashflowKind.ExternalWithdrawal,
            DepotCashflowClassifier.Classify(Tx(DefaultAccount, -200m, "Transfer", 3), isDefaultSettlementAccount: true));
    }

    [Theory]
    [InlineData("Transfer", false)]
    [InlineData("Direct Debit", false)]
    [InlineData("Direct Debit", true)]
    [InlineData("Card transaction", false)]
    public void Alltagsumsaetze_werden_ignoriert(string type, bool isDefault)
    {
        var groceryShopping = Tx(GiroAccount, -59.49m, type, 25);

        Assert.Equal(DepotCashflowKind.Other, DepotCashflowClassifier.Classify(groceryShopping, isDefault));
    }

    // ---- Berechnung ----

    [Fact]
    public void Ohne_Standard_Verrechnungskonto_gibt_es_keine_Kennzahlen()
    {
        var result = DepotPerformanceCalculator.Calculate([Snapshot(1, At(1), 1000m)], new Dictionary<long, decimal>(), []);

        Assert.Empty(result);
    }

    [Fact]
    public void Der_erste_Snapshot_ist_der_Bezugspunkt_sein_Gesamtwert_das_Startkapital()
    {
        var result = Calculate([Snapshot(1, At(1), 10_000m), Snapshot(2, At(2), 10_500m)], latestBalance: 2000m);

        Assert.Equal(12_000m, result[0].NetInvestedCapital); // Positionen 10.000 + Guthaben 2.000
        Assert.Equal(12_000m, result[1].NetInvestedCapital);
        Assert.All(result, p => Assert.Equal(2000m, p.SettlementCash));
    }

    [Fact]
    public void Kauf_aus_vorhandenem_Guthaben_ist_neutral_wenn_Depot_und_Buchung_am_selben_Tag_erscheinen()
    {
        // Nachgestellt nach den echten Munich-Re-/BASF-Daten (28.09.): Aktien stehen am Ausführungstag im Depot,
        // das Guthaben sinkt zum selben Buchungstag. Valuta (hier 4.) ist für das Modell ohne Belang.
        var buy = Tx(DefaultAccount, -3000m, "Securities", 2, 4);
        var result = Calculate(
            [
                Snapshot(1, At(1, 12), 10_000m),   // Guthaben 3.000, Gesamtwert 13.000 = Startkapital
                Snapshot(2, At(2, 20), 13_000m),   // Aktien im Depot, Guthaben 0
                Snapshot(3, At(3, 20), 13_000m),
            ],
            latestBalance: 0m,
            buy);

        Assert.Equal([3000m, 0m, 0m], result.Select(p => p.SettlementCash));
        Assert.All(result, p => Assert.Equal(13_000m, p.NetInvestedCapital)); // kein Euro von außen
        Assert.All(result.Skip(1), p => Assert.Equal(0m, p.TimeWeightedReturnPercent)); // kein Ausschlag durch den Handel
    }

    [Fact]
    public void Verkauf_ueber_das_Verrechnungskonto_verschiebt_nur_zwischen_Positionen_und_Guthaben()
    {
        var sale = Tx(DefaultAccount, 340m, "Securities", 2, 4);
        var result = Calculate(
            [Snapshot(1, At(1, 12), 10_000m), Snapshot(2, At(2, 20), 9_660m)],
            latestBalance: 1340m, // vorher 1.000, danach 1.340
            sale);

        Assert.Equal([1000m, 1340m], result.Select(p => p.SettlementCash));
        Assert.All(result, p => Assert.Equal(11_000m, p.NetInvestedCapital));
        Assert.Equal(0m, result[1].TimeWeightedReturnPercent);
    }

    [Fact]
    public void Externe_Einzahlung_aufs_Verrechnungskonto_erhoeht_das_Kapital_und_ist_kein_Gewinn()
    {
        var deposit = Tx(DefaultAccount, 1000m, "Transfer", 2);
        var result = Calculate([Snapshot(1, At(1), 10_000m), Snapshot(2, At(2), 10_000m)], latestBalance: 1000m, deposit);

        Assert.Equal(10_000m, result[0].NetInvestedCapital);
        Assert.Equal(11_000m, result[1].NetInvestedCapital);
        Assert.Equal(0m, result[1].TimeWeightedReturnPercent);
    }

    [Fact]
    public void Sparplan_vom_Girokonto_ist_eine_Einzahlung_am_Buchungstag()
    {
        var savingsPlan = Tx(GiroAccount, -100m, "Securities", 2, 4);
        var result = Calculate([Snapshot(1, At(1, 12), 10_000m), Snapshot(2, At(2, 12), 10_100m)], latestBalance: 0m, savingsPlan);

        Assert.Equal(10_100m, result[1].NetInvestedCapital);
        Assert.Equal(0m, result[1].TimeWeightedReturnPercent);
    }

    [Fact]
    public void Kursgewinn_ohne_Bewegung_schlaegt_sich_in_der_TWR_nieder_das_Kapital_bleibt()
    {
        var result = Calculate([Snapshot(1, At(1), 1000m), Snapshot(2, At(2), 1100m)], latestBalance: 0m);

        Assert.Equal(1000m, result[1].NetInvestedCapital);
        Assert.Equal(10m, result[1].TimeWeightedReturnPercent);
    }

    [Fact]
    public void Dividende_erhoeht_Guthaben_und_Rendite_aber_nicht_das_Kapital()
    {
        var dividend = Tx(DefaultAccount, 55m, "Interest / Dividends", 2);
        var result = Calculate([Snapshot(1, At(1), 1000m), Snapshot(2, At(2), 1000m)], latestBalance: 55m, dividend);

        Assert.Equal(1000m, result[1].NetInvestedCapital);
        Assert.Equal(55m, result[1].DividendsReceived);
        Assert.Equal(5.5m, result[1].TimeWeightedReturnPercent);
    }

    [Fact]
    public void Umsaetze_am_oder_vor_dem_Bezugstag_zaehlen_nicht_mehr_zum_Kapital()
    {
        var earlierSavingsPlan = Tx(GiroAccount, -500m, "Securities", 1);
        var earlierDividend = Tx(DefaultAccount, 40m, "Interest / Dividends", 1);
        var result = Calculate([Snapshot(1, At(1, 12), 10_000m)], latestBalance: 40m, earlierSavingsPlan, earlierDividend);

        Assert.Equal(10_040m, result[0].NetInvestedCapital);
        Assert.Equal(0m, result[0].DividendsReceived);
    }

    [Fact]
    public void Alle_Buchungsarten_auf_dem_Verrechnungskonto_gehen_in_die_Guthaben_Rueckrechnung_ein()
    {
        // Gebühren zählen nicht als Kapitalbewegung, mindern aber das Guthaben (= sind ein echter Verlust).
        var fee = Tx(DefaultAccount, -10m, "Bank fees", 2);
        var result = Calculate([Snapshot(1, At(1), 1000m), Snapshot(2, At(2), 1000m)], latestBalance: 90m, fee);

        Assert.Equal([100m, 90m], result.Select(p => p.SettlementCash));
        Assert.Equal(1100m, result[1].NetInvestedCapital);
        Assert.True(result[1].TimeWeightedReturnPercent < 0);
    }

    [Fact]
    public void TWR_verkettet_Tagesrenditen_und_bereinigt_um_eine_Einzahlung()
    {
        var deposit = Tx(GiroAccount, -100m, "Securities", 3);
        var result = Calculate(
            [Snapshot(1, At(1), 1000m), Snapshot(2, At(2), 1100m), Snapshot(3, At(3), 1250m)], latestBalance: 0m, deposit);

        Assert.Equal(0m, result[0].TimeWeightedReturnPercent); // Bezugspunkt
        Assert.Equal(10m, result[1].TimeWeightedReturnPercent);
        Assert.Equal(15m, result[2].TimeWeightedReturnPercent); // 1,10 * ((1250-1100-100)/1100 + 1) = 1,15
    }

    [Fact]
    public void Kursbewegung_im_Verlauf_des_Bezugstags_zaehlt_zur_TWR()
    {
        var result = Calculate([Snapshot(1, At(1, 8), 1000m), Snapshot(2, At(1, 20), 950m)], latestBalance: 0m);

        Assert.Equal(-5m, result[1].TimeWeightedReturnPercent);
    }

    [Fact]
    public void Mehrere_Snapshots_am_selben_Tag_beziehen_sich_auf_den_Vortag_und_nur_der_letzte_zaehlt_fuer_die_Kette()
    {
        var result = Calculate(
            [
                Snapshot(1, At(1), 1000m),
                Snapshot(2, At(2, 8), 1200m),   // Zwischenstand, wird durch den letzten des Tages ersetzt
                Snapshot(3, At(2, 20), 1100m),
                Snapshot(4, At(3, 12), 1210m),
            ],
            latestBalance: 0m);

        Assert.Equal(20m, result[1].TimeWeightedReturnPercent);
        Assert.Equal(10m, result[2].TimeWeightedReturnPercent);
        Assert.Equal(21m, result[3].TimeWeightedReturnPercent); // 1,10 * 1,10
    }
}
