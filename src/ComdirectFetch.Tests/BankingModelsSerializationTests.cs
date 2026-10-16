using System.Text.Json;
using ComdirectFetch.Api;

namespace ComdirectFetch.Tests;

/// <summary>
/// Regressionstest für die gegen die offizielle comdirect-Doku korrigierten Feldtypen (Beträge
/// als JSON-String) sowie für den beim Live-Test entdeckten Fall, dass bookingDate in der
/// echten API-Antwort ein einfacher String ist statt des dokumentierten {"date": "..."}-Objekts.
/// </summary>
public class BankingModelsSerializationTests
{
    [Fact]
    public void AmountValue_liest_Betrag_aus_JSON_String()
    {
        var json = """{"value": "999.99", "unit": "EUR"}""";

        var amount = JsonSerializer.Deserialize<AmountValue>(json)!;

        Assert.Equal(999.99m, amount.Value);
        Assert.Equal("EUR", amount.Unit);
    }

    [Fact]
    public void TransactionEntry_liest_BookingDate_als_einfachen_String_wie_in_der_Live_API()
    {
        var json = """
            {
                "reference": "ref-1",
                "bookingDate": "2026-01-15",
                "valutaDate": "2026-01-16",
                "amount": {"value": "-42.50", "unit": "EUR"}
            }
            """;

        var transaction = JsonSerializer.Deserialize<TransactionEntry>(json)!;

        Assert.Equal(new DateOnly(2026, 1, 15), transaction.BookingDate);
        Assert.Equal("2026-01-16", transaction.ValutaDate);
        Assert.Equal(-42.50m, transaction.Amount.Value);
    }

    [Fact]
    public void TransactionEntry_liest_BookingDate_auch_als_dokumentiertes_verschachteltes_Objekt()
    {
        var json = """
            {
                "reference": "ref-2",
                "bookingDate": {"date": "2026-02-20"},
                "amount": {"value": "10.00", "unit": "EUR"}
            }
            """;

        var transaction = JsonSerializer.Deserialize<TransactionEntry>(json)!;

        Assert.Equal(new DateOnly(2026, 2, 20), transaction.BookingDate);
    }

    [Fact]
    public void Instrument_liest_InstrumentType_aus_verschachteltem_StaticData_Objekt()
    {
        // Struktur laut offizieller Swagger-Doku (GitHub-Issue #4): instrument.staticData.instrumentType.
        var json = """
            {
                "isin": "IE00B4L5Y983",
                "wkn": "A0RPWH",
                "name": "iShares Core MSCI World UCITS ETF",
                "staticData": {"instrumentType": "ETF", "notation": "XXC"}
            }
            """;

        var instrument = JsonSerializer.Deserialize<Instrument>(json)!;

        Assert.Equal("ETF", instrument.StaticData?.InstrumentType);
    }

    [Fact]
    public void Instrument_ohne_StaticData_liefert_null_InstrumentType()
    {
        var json = """{"isin": "IE00B4L5Y983", "name": "Test"}""";

        var instrument = JsonSerializer.Deserialize<Instrument>(json)!;

        Assert.Null(instrument.StaticData);
    }
}

/// <summary>Depot-Transaktion im live beobachteten Format (Issue #19), synthetische Werte.</summary>
public class DepotTransactionSerializationTests
{
    [Fact]
    public void DepotTransactionEntry_liest_Live_Format()
    {
        var json = """
            {
                "transactionId": "0000000000000000000000000000000A",
                "bookingStatus": "BOOKED",
                "bookingDate": "2026-01-16",
                "businessDate": "2026-01-15",
                "quantity": {"value": "10", "unit": "XXX"},
                "instrumentId": "X",
                "instrument": {"wkn": "A0B1C2", "isin": "DE0000000001", "name": "Beispiel AG",
                    "staticData": {"instrumentType": "SHARE"}},
                "executionPrice": {"value": "100", "unit": "EUR"},
                "transactionValue": {"value": "1009.9", "unit": "EUR"},
                "transactionDirection": "IN",
                "transactionType": "BUY"
            }
            """;

        var entry = JsonSerializer.Deserialize<DepotTransactionEntry>(json)!;

        Assert.Equal(new DateOnly(2026, 1, 15), entry.BusinessDate);
        Assert.Equal(new DateOnly(2026, 1, 16), entry.BookingDate);
        Assert.Equal(10m, entry.Quantity.Value);
        Assert.Equal(1009.9m, entry.TransactionValue!.Value);
        Assert.Equal("SHARE", entry.Instrument!.StaticData!.InstrumentType);
        Assert.Equal("IN", entry.TransactionDirection);
    }
}
