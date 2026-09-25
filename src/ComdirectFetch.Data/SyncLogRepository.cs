using Dapper;
using ComdirectFetch.Domain;

namespace ComdirectFetch.Data;

/// <summary>Protokoll-Zugriff auf sync_log (KONZEPT.md Abschnitt 3/5/8).</summary>
public sealed class SyncLogRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<long> InsertAsync(SyncLogEntry entry, CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            INSERT INTO sync_log
                (data_kind, account_id, portfolio_id, application_version, started_at, finished_at, status, error_message)
            VALUES
                (@DataKind, @AccountId, @PortfolioId, @ApplicationVersion, @StartedAt, @FinishedAt, @Status, @ErrorMessage);
            SELECT LAST_INSERT_ID();
            """;

        // Enums explizit als String übergeben: Dapper wandelt Enum-Parameter sonst intern in
        // ihren zugrunde liegenden Zahlentyp um, bevor ein registrierter TypeHandler geprüft
        // wird – das passt nicht zu den MySQL-ENUM-Spalten ("Data truncated for column").
        return await connection.ExecuteScalarAsync<long>(sql, new
        {
            DataKind = entry.DataKind.ToString(),
            entry.AccountId,
            entry.PortfolioId,
            entry.ApplicationVersion,
            entry.StartedAt,
            entry.FinishedAt,
            Status = entry.Status.ToString(),
            entry.ErrorMessage,
        });
    }

    public async Task CompleteAsync(
        long id,
        SyncStatus status,
        DateTimeOffset finishedAt,
        string? errorMessage = null,
        CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            UPDATE sync_log
            SET status = @Status, finished_at = @FinishedAt, error_message = @ErrorMessage
            WHERE id = @Id;
            """;

        await connection.ExecuteAsync(sql, new { Id = id, Status = status.ToString(), FinishedAt = finishedAt, ErrorMessage = errorMessage });
    }

    /// <summary>Jüngster Eintrag – dient z. B. dazu, im Health-Status "Freigabe erforderlich" anzuzeigen.</summary>
    public async Task<SyncLogEntry?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT id AS Id, data_kind AS DataKind, account_id AS AccountId, portfolio_id AS PortfolioId,
                   application_version AS ApplicationVersion, started_at AS StartedAt, finished_at AS FinishedAt,
                   status AS Status, error_message AS ErrorMessage
            FROM sync_log
            ORDER BY started_at DESC
            LIMIT 1;
            """;

        return await connection.QueryFirstOrDefaultAsync<SyncLogEntry>(sql);
    }
}
