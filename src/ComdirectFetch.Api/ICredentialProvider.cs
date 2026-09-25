namespace ComdirectFetch.Api;

/// <summary>
/// Liefert Zugangsnummer/PIN für den initialen Login (KONZEPT.md Abschnitt 10 B, "Sichere
/// Ablage der comdirect-Zugangsdaten"). Die produktive Implementierung lebt im Worker-Projekt
/// (braucht DB-Zugriff über CredentialRepository) - Api kennt nur diese Abstraktion, damit
/// ComdirectAuthClient nicht mehr direkt an ComdirectApiOptions.Username/Password gebunden
/// ist und die Werte zur Laufzeit statt beim DI-Container-Bau aufgelöst werden können.
/// </summary>
public interface ICredentialProvider
{
    Task<(string Username, string Password)> GetLoginCredentialsAsync(CancellationToken cancellationToken = default);
}
