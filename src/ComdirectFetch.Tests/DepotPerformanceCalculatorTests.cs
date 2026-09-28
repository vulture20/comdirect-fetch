using ComdirectFetch.Domain;

namespace ComdirectFetch.Tests;

public class DepotPerformanceCalculatorTests
{
    private static Transaction MakeTransaction(decimal amount, DateOnly bookingDate, string? transactionType = null) => new()
    {
        AccountId = 3,
        ComdirectReference = "ref-1",
        BookingDate = bookingDate,
        Amount = amount,
        Currency = "EUR",
        TransactionType = transactionType,
        FirstSeenAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Klassifiziert_Dividende_ueber_transaction_type_unabhaengig_vom_Vorzeichen()
    {
        var dividend = MakeTransaction(55.59m, new DateOnly(2026, 8, 3), transactionType: "Interest / Dividends");

        Assert.Equal(DepotCashflowKind.Dividend, DepotCashflowClassifier.Classify(dividend));
    }

    [Fact]
    public void Klassifiziert_negativen_Betrag_ohne_Dividenden_Typ_als_externe_Einzahlung()
    {
        var buy = MakeTransaction(-24.97m, new DateOnly(2026, 9, 24), transactionType: "Securities");

        Assert.Equal(DepotCashflowKind.ExternalDeposit, DepotCashflowClassifier.Classify(buy));
    }

    [Fact]
    public void Klassifiziert_positiven_Betrag_vom_Typ_Securities_als_externe_Auszahlung()
    {
        var sale = MakeTransaction(500m, new DateOnly(2026, 9, 24), transactionType: "Securities");

        Assert.Equal(DepotCashflowKind.ExternalWithdrawal, DepotCashflowClassifier.Classify(sale));
    }

    [Fact]
    public void Ignoriert_kontofremde_Umsaetze_auf_einem_verknuepften_Konto()
    {
        // z. B. ein Lebensmitteleinkauf auf dem Girokonto, das auch Sparplan-Käufe abwickelt -
        // hat nichts mit dem Depot zu tun und darf den Kapitaleinsatz nicht verfälschen.
        var groceryShopping = MakeTransaction(-59.49m, new DateOnly(2026, 9, 25), transactionType: "Direct Debit");

        Assert.Equal(DepotCashflowKind.Other, DepotCashflowClassifier.Classify(groceryShopping));
    }

    [Fact]
    public void Netto_Kapitaleinsatz_summiert_Einzahlungen_Auszahlungen_und_Dividenden_getrennt_und_ignoriert_Anderes()
    {
        Transaction[] transactions =
        [
            MakeTransaction(-1000m, new DateOnly(2026, 1, 1), "Securities"), // Kauf: 1000 eingesetzt
            MakeTransaction(-500m, new DateOnly(2026, 3, 1), "Securities"),  // Kauf: weitere 500 eingesetzt
            MakeTransaction(200m, new DateOnly(2026, 6, 1), "Securities"),   // Verkauf: 200 Erlös -> mindert Einsatz
            MakeTransaction(50m, new DateOnly(2026, 7, 1), "Interest / Dividends"), // Dividende, kein Kapitaleinsatz
            MakeTransaction(-30m, new DateOnly(2026, 8, 1), "Direct Debit"), // kontofremd, muss ignoriert werden
        ];

        var result = DepotPerformanceCalculator.CalculateNetInvestedCapital(currentDepotValue: 1400m, transactions);

        Assert.Equal(1300m, result.NetInvestedCapital); // 1000 + 500 - 200
        Assert.Equal(50m, result.DividendsReceived);
        Assert.Equal(1400m + 50m - 1300m, result.ProfitLoss); // 150
        Assert.NotNull(result.ReturnPercent);
        Assert.Equal(150m / 1300m * 100m, result.ReturnPercent!.Value);
    }

    [Fact]
    public void Netto_Kapitaleinsatz_liefert_kein_Rendite_Prozent_ohne_eingesetztes_Kapital()
    {
        var result = DepotPerformanceCalculator.CalculateNetInvestedCapital(currentDepotValue: 0m, []);

        Assert.Equal(0m, result.NetInvestedCapital);
        Assert.Null(result.ReturnPercent);
    }

    [Fact]
    public void Konsolidiert_mehrere_Snapshots_am_selben_Tag_auf_den_letzten_Wert()
    {
        List<(DateTimeOffset Timestamp, decimal TotalValue)> snapshots =
        [
            (new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero), 1000m),
            (new DateTimeOffset(2026, 1, 1, 18, 0, 0, TimeSpan.Zero), 1010m),
            (new DateTimeOffset(2026, 1, 2, 9, 0, 0, TimeSpan.Zero), 1050m),
        ];

        var daily = DepotPerformanceCalculator.ConsolidateToDailyLastValue(snapshots);

        Assert.Equal(2, daily.Count);
        Assert.Equal(new DailyDepotValuation(new DateOnly(2026, 1, 1), 1010m), daily[0]);
        Assert.Equal(new DailyDepotValuation(new DateOnly(2026, 1, 2), 1050m), daily[1]);
    }

    [Fact]
    public void TWR_verkettet_Tagesrenditen_und_bereinigt_um_eine_zwischenzeitliche_Einzahlung()
    {
        List<DailyDepotValuation> daily =
        [
            new(new DateOnly(2026, 1, 1), 1000m),
            new(new DateOnly(2026, 1, 2), 1100m), // +10% ohne Cashflow
            new(new DateOnly(2026, 1, 3), 1250m), // +150 brutto, davon 100 Einzahlung am 3.1.
        ];
        Transaction[] transactions =
        [
            MakeTransaction(-100m, new DateOnly(2026, 1, 3), "Securities"),
            MakeTransaction(-40m, new DateOnly(2026, 1, 3), "Direct Debit"), // kontofremd, muss ignoriert werden
        ];

        var result = DepotPerformanceCalculator.CalculateTimeWeightedReturn(daily, transactions);

        Assert.NotNull(result);
        Assert.Equal(2, result!.DaysConsidered);
        Assert.Equal(15.0m, result.ReturnPercent); // 1.10 * ((1250-1100-100)/1100 + 1) = 1.15
    }

    [Fact]
    public void TWR_liefert_null_bei_weniger_als_zwei_Tageswerten()
    {
        List<DailyDepotValuation> daily = [new(new DateOnly(2026, 1, 1), 1000m)];

        var result = DepotPerformanceCalculator.CalculateTimeWeightedReturn(daily, []);

        Assert.Null(result);
    }
}
