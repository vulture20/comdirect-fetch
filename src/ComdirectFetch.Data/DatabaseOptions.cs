using MySqlConnector;

namespace ComdirectFetch.Data;

/// <summary>
/// Verbindungsdaten zur vom Nutzer extern bereitgestellten MariaDB (KONZEPT.md Abschnitt 2/4).
/// Der Betrieb der Datenbank selbst ist nicht Teil dieses Projekts.
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public required string Host { get; set; }
    public int Port { get; set; } = 3306;
    public required string Name { get; set; }
    public required string User { get; set; }
    public required string Password { get; set; }

    public string BuildConnectionString() => new MySqlConnectionStringBuilder
    {
        Server = Host,
        Port = (uint)Port,
        Database = Name,
        UserID = User,
        Password = Password,
    }.ConnectionString;
}
