using ComdirectFetch.Domain;

namespace ComdirectFetch.Tests;

/// <summary>Synthetische Werte; das Verhalten (transactionValue inkl. Gebühren) ist live geprüft, siehe TradeFeeCalculator.</summary>
public class TradeFeeCalculatorTests
{
    [Fact]
    public void Kauf_Gebuehr_ist_Abrechnungsbetrag_minus_Kurswert()
    {
        // 10 Stück zu 100,00 = 1.000,00 Kurswert, belastet 1.009,90.
        Assert.Equal(9.90m, TradeFeeCalculator.Calculate("IN", 10m, 100m, 1009.90m));
    }

    [Fact]
    public void Verkauf_Gebuehr_ist_Kurswert_minus_Gutschrift()
    {
        Assert.Equal(9.90m, TradeFeeCalculator.Calculate("OUT", 10m, 100m, 990.10m));
    }

    [Fact]
    public void Gebuehrenfreier_Sparplan_ergibt_null_Euro_trotz_Rundungsrauschen()
    {
        // Bruchstücke: Stück x Kurs weicht im Sub-Cent-Bereich vom abgerechneten Betrag ab.
        Assert.Equal(0m, TradeFeeCalculator.Calculate("IN", 0.432152m, 115.70m, 50.00m));
    }

    [Fact]
    public void Prozentuale_Sparplan_Gebuehr_wird_auf_Cent_gerundet()
    {
        // 50,00 Sparrate, davon 1,5 % = 0,75 Gebühr, 49,25 angelegt.
        Assert.Equal(0.75m, TradeFeeCalculator.Calculate("IN", 0.425645m, 115.70m, 50.00m));
    }

    [Theory]
    [InlineData(null, 100.0, 1000.0)]
    [InlineData("IN", null, 1000.0)]
    [InlineData("IN", 100.0, null)]
    [InlineData("TRANSFER", 100.0, 1000.0)]
    public void Ohne_Kurs_Betrag_oder_bekannte_Richtung_keine_Gebuehr(string? direction, double? price, double? value)
    {
        Assert.Null(TradeFeeCalculator.Calculate(direction, 10m, (decimal?)price, (decimal?)value));
    }
}
