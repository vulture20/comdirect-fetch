using ComdirectFetch.Domain;

namespace ComdirectFetch.Tests;

/// <summary>Synthetische Werte; Zuordnungsregeln siehe SavingsPlanFlowDating (Issue #18).</summary>
public class SavingsPlanFlowDatingTests
{
    private static Transaction Booking(long id, decimal amount, int day, string text = "01BEISPIEL ETF   02WPKNR: A0B1C2  ISIN: DE0000000001") => new()
    {
        Id = id,
        AccountId = 1,
        ComdirectReference = $"ref-{id}",
        BookingDate = new DateOnly(2026, 9, day),
        Amount = amount,
        Currency = "EUR",
        BookingText = text,
        TransactionType = "Securities",
        FirstSeenAt = DateTimeOffset.UnixEpoch,
    };

    private static DepotTransaction Depot(string id, string direction, decimal value, int businessDay, int bookingDay, string wkn = "A0B1C2", string? isin = "DE0000000001") => new()
    {
        PortfolioId = 1,
        ComdirectTransactionId = id,
        BookingStatus = "BOOKED",
        BusinessDate = new DateOnly(2026, 9, businessDay),
        BookingDate = new DateOnly(2026, 9, bookingDay),
        Direction = direction,
        Wkn = wkn,
        Isin = isin,
        Quantity = 1m,
        TransactionValue = value,
        FirstSeenAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void Kauf_bekommt_den_Depot_Buchungstag()
    {
        var result = SavingsPlanFlowDating.Resolve([Booking(1, -50m, 15)], [Depot("d1", "IN", 50m, 15, 16)]);

        Assert.Equal(new DateOnly(2026, 9, 16), result[1]);
    }

    [Fact]
    public void Zuordnung_auch_nur_ueber_die_ISIN()
    {
        var result = SavingsPlanFlowDating.Resolve(
            [Booking(1, -50m, 15, "01BEISPIEL ETF ISIN: DE0000000001")], [Depot("d1", "IN", 50m, 15, 16, wkn: "ZZZZZZ")]);

        Assert.True(result.ContainsKey(1));
    }

    [Theory]
    [InlineData("OUT", 50.0, 15)]   // falsche Richtung
    [InlineData("IN", 49.26, 15)]   // anderer Betrag
    [InlineData("IN", 50.0, 25)]    // zu weit entfernt
    public void Kein_Treffer_bei_abweichenden_Merkmalen(string direction, double value, int businessDay)
    {
        var result = SavingsPlanFlowDating.Resolve([Booking(1, -50m, 15)], [Depot("d1", direction, (decimal)value, businessDay, businessDay + 1)]);

        Assert.Empty(result);
    }

    [Fact]
    public void Kein_Treffer_ohne_WKN_oder_ISIN_im_Buchungstext()
    {
        var result = SavingsPlanFlowDating.Resolve([Booking(1, -50m, 15, "01ANDERES PAPIER")], [Depot("d1", "IN", 50m, 15, 16)]);

        Assert.Empty(result);
    }

    [Fact]
    public void Jede_Depot_Transaktion_wird_nur_einmal_vergeben()
    {
        // Zwei gleiche Sparraten in einem Monat, aber nur eine Depot-Transaktion: die frühere Buchung bekommt sie.
        var result = SavingsPlanFlowDating.Resolve([Booking(1, -50m, 15), Booking(2, -50m, 16)], [Depot("d1", "IN", 50m, 15, 16)]);

        Assert.Single(result);
        Assert.True(result.ContainsKey(1));
    }

    [Fact]
    public void Bei_Gleichstand_keine_Zuordnung()
    {
        var result = SavingsPlanFlowDating.Resolve(
            [Booking(1, -50m, 15)], [Depot("d1", "IN", 50m, 14, 15), Depot("d2", "IN", 50m, 16, 17)]);

        Assert.Empty(result);
    }

    [Fact]
    public void Der_naechstgelegene_Handelstag_gewinnt()
    {
        var result = SavingsPlanFlowDating.Resolve(
            [Booking(1, -50m, 15)], [Depot("d1", "IN", 50m, 15, 16), Depot("d2", "IN", 50m, 18, 19)]);

        Assert.Equal(new DateOnly(2026, 9, 16), result[1]);
    }
}
