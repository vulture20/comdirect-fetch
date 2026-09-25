using System.Text;
using ComdirectFetch.Domain;

namespace ComdirectFetch.Tests;

/// <summary>
/// Regressionstests für GitHub-Issue #8: comdirect-Fehlertexte mit Umlauten wurden in Logs
/// teils als "�" statt z. B. "ü" dargestellt, weil die Response-Bytes tatsächlich
/// Latin-1-kodiert sind, ohne dass ein verlässlicher Charset-Header das anzeigt.
/// </summary>
public class TextDecodingTests
{
    [Fact]
    public void Dekodiert_valide_UTF8_Bytes_unverändert()
    {
        const string original = "Die erlaubte Anzahl der Anfragen ist überschritten";
        var utf8Bytes = Encoding.UTF8.GetBytes(original);

        var result = TextDecoding.DecodeUtf8WithLatin1Fallback(utf8Bytes);

        Assert.Equal(original, result);
    }

    [Fact]
    public void Faellt_bei_Latin1_kodierten_Bytes_auf_Latin1_zurueck_statt_Replacement_Zeichen_zu_liefern()
    {
        const string original = "Die erlaubte Anzahl der Anfragen ist überschritten";
        var latin1Bytes = Encoding.Latin1.GetBytes(original);

        var result = TextDecoding.DecodeUtf8WithLatin1Fallback(latin1Bytes);

        Assert.Equal(original, result);
        Assert.DoesNotContain('�', result);
    }

    [Fact]
    public void Liefert_leeren_String_fuer_leere_Bytes()
    {
        var result = TextDecoding.DecodeUtf8WithLatin1Fallback([]);

        Assert.Equal(string.Empty, result);
    }
}
