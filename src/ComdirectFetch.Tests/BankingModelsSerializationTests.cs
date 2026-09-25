using System.Text.Json;
using ComdirectFetch.Api;

namespace ComdirectFetch.Tests;

/// <summary>
/// Regressionstest für die gegen die offizielle comdirect-Doku korrigierten Feldtypen:
/// Beträge kommen als JSON-String, bookingDate als verschachteltes {"date": "..."}-Objekt.
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
    public void TransactionEntry_liest_verschachteltes_BookingDate_und_flaches_ValutaDate()
    {
        var json = """
            {
                "reference": "ref-1",
                "bookingDate": {"date": "2026-01-15"},
                "valutaDate": "2026-01-16",
                "amount": {"value": "-42.50", "unit": "EUR"}
            }
            """;

        var transaction = JsonSerializer.Deserialize<TransactionEntry>(json)!;

        Assert.Equal(new DateOnly(2026, 1, 15), transaction.BookingDate.Date);
        Assert.Equal("2026-01-16", transaction.ValutaDate);
        Assert.Equal(-42.50m, transaction.Amount.Value);
    }
}
