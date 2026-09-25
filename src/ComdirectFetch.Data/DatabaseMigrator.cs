using System.Reflection;
using DbUp;
using DbUp.Engine;
using Microsoft.Extensions.Logging;

namespace ComdirectFetch.Data;

/// <summary>
/// Wendet die append-only Migrationsskripte aus db/migrations/ auf die vom Nutzer
/// bereitgestellte, extern betriebene MariaDB an (KONZEPT.md Abschnitt 8). Läuft beim
/// Start des Fetch-Diensts; bereits angewendete Skripte werden von DbUp selbst in der
/// Zieldatenbank protokolliert und übersprungen.
/// </summary>
public sealed class DatabaseMigrator(ILogger<DatabaseMigrator> logger)
{
    public void MigrateToLatest(string connectionString)
    {
        var upgrader = DeployChanges.To
            .MySqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(Assembly.GetExecutingAssembly())
            .LogTo(new DbUpLoggerAdapter(logger))
            .Build();

        var result = upgrader.PerformUpgrade();

        if (!result.Successful)
        {
            throw new InvalidOperationException(
                $"Datenbank-Migration fehlgeschlagen bei Skript '{result.ErrorScript?.Name}'.",
                result.Error);
        }

        logger.LogInformation(
            "Datenbank-Schema aktuell. {Count} neu angewendete Migration(en).",
            result.Scripts.Count());
    }

    private sealed class DbUpLoggerAdapter(ILogger logger) : DbUp.Engine.Output.IUpgradeLog
    {
        public void LogTrace(string format, params object[] args) => logger.LogTrace(format, args);
        public void LogDebug(string format, params object[] args) => logger.LogDebug(format, args);
        public void LogInformation(string format, params object[] args) => logger.LogInformation(format, args);
        public void LogWarning(string format, params object[] args) => logger.LogWarning(format, args);
        public void LogError(string format, params object[] args) => logger.LogError(format, args);
        public void LogError(Exception ex, string format, params object[] args) => logger.LogError(ex, format, args);
    }
}
