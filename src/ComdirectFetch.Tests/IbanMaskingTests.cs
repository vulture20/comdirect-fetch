using ComdirectFetch.Domain;

namespace ComdirectFetch.Tests;

public class IbanMaskingTests
{
    [Fact]
    public void Maskiert_lange_Iban_auf_erste_und_letzte_vier_Stellen()
    {
        Assert.Equal("DE00...0099", IbanMasking.Mask("DE00123456780000000099"));
    }

    [Fact]
    public void Maskiert_kurze_Zeichenkette_komplett()
    {
        Assert.Equal("********", IbanMasking.Mask("DE33ABCD"));
    }

    [Fact]
    public void Gibt_null_oder_leer_unveraendert_zurueck()
    {
        Assert.Null(IbanMasking.Mask(null!));
        Assert.Equal("", IbanMasking.Mask(""));
    }
}
