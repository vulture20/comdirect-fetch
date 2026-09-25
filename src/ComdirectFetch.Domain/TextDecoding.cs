using System.Text;

namespace ComdirectFetch.Domain;

/// <summary>
/// Dekodiert Bytes robust als Text, wenn die deklarierte/erwartete Kodierung nicht verlässlich
/// ist (GitHub-Issue #8: comdirect liefert Fehlertexte teils ohne oder mit irreführendem
/// Charset-Header, obwohl die Bytes tatsächlich Latin-1/ISO-8859-1-kodiert sind, nicht UTF-8 -
/// z. B. wurde "überschritten" zu "�berschritten"). .NETs Standard-Dekodierung für
/// HttpContent.ReadAsStringAsync geht ohne Charset-Header von UTF-8 aus und ersetzt ungültige
/// Byte-Folgen lautlos durch das Replacement-Zeichen U+FFFD ("�"), statt einen Fehler zu
/// melden oder eine andere Kodierung zu versuchen.
/// </summary>
public static class TextDecoding
{
    private static readonly Encoding StrictUtf8 = Encoding.GetEncoding(
        "utf-8", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    /// <summary>
    /// Versucht zuerst strikte UTF-8-Dekodierung (wirft bei ungültigen Byte-Folgen, statt sie
    /// stillschweigend durch U+FFFD zu ersetzen); schlägt das fehl, wird als Latin-1 dekodiert -
    /// das deckt jedes Byte ab (kein weiterer Fallback nötig) und liefert für tatsächlich
    /// Latin-1-kodierte Texte (wie comdirects Fehlertexte) das korrekte Ergebnis, statt eines
    /// Replacement-Zeichens. Valide UTF-8-Bytes werden nie fälschlich als ungültig erkannt,
    /// daher ändert dieser Fallback nichts am Verhalten für tatsächlich UTF-8-kodierte Antworten.
    /// </summary>
    public static string DecodeUtf8WithLatin1Fallback(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }
}
