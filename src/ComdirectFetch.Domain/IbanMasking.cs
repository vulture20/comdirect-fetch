namespace ComdirectFetch.Domain;

/// <summary>
/// Reine, DB-unabhängige Maskierung von IBANs für Log-Ausgaben (Datenschutz-Review: volle IBANs
/// gehören nicht unmaskiert in Logdateien, die andere Aufbewahrungs-/Zugriffsregeln haben können
/// als die DB). Zeigt Länderkennung+Prüfziffer sowie die letzten 4 Stellen, genug um Konten in
/// Logzeilen auseinanderzuhalten, ohne die volle Kontonummer preiszugeben.
/// </summary>
public static class IbanMasking
{
    public static string Mask(string iban)
    {
        if (string.IsNullOrEmpty(iban))
        {
            return iban;
        }

        return iban.Length <= 8 ? new string('*', iban.Length) : $"{iban[..4]}...{iban[^4..]}";
    }
}
