using System.Security.Cryptography;
using System.Text;

namespace ComdirectFetch.Worker;

/// <summary>
/// Prüft den Authorization-Header für die neuen /admin/*-Endpunkte (GitHub-Issue #13, KONZEPT.md
/// Abschnitt 12) gegen Admin__Password per HTTP Basic Auth. Bewusst kein neues NuGet-Paket/keine
/// ASP.NET-Core-Authentication-Scheme-Konfiguration - ein einfacher Header-Check reicht für dieses
/// Single-Operator-Szenario. Reine, DB-/Request-unabhängige Funktion, damit sie ohne einen echten
/// HTTP-Request testbar ist (wie CategorizationLogic).
/// </summary>
public static class AdminAuth
{
    public static bool TryValidate(string? authorizationHeader, string expectedPassword)
    {
        const string prefix = "Basic ";
        if (string.IsNullOrEmpty(authorizationHeader) || !authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorizationHeader[prefix.Length..]));
        }
        catch (FormatException)
        {
            return false;
        }

        var separatorIndex = decoded.IndexOf(':');
        if (separatorIndex < 0)
        {
            return false;
        }

        // Konstante Laufzeit beim Passwortvergleich, damit die Antwortzeit nicht verrät, wie
        // viele Zeichen bereits korrekt geraten wurden. Der Benutzername vor dem ":" wird bewusst
        // ignoriert - es gibt nur ein gemeinsames Passwort, keine Benutzerverwaltung.
        var password = decoded[(separatorIndex + 1)..];
        var actualBytes = Encoding.UTF8.GetBytes(password);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedPassword);
        return actualBytes.Length == expectedBytes.Length && CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }
}
