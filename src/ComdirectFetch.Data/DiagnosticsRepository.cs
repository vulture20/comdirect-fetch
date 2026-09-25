using Dapper;

namespace ComdirectFetch.Data;

/// <summary>Betriebs-/Test-Hilfsmittel: Zeilenanzahl je Tabelle, für den manuellen Live-Test ohne direkten DB-Zugriff.</summary>
public sealed class DiagnosticsRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyDictionary<string, long>> GetTableCountsAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT 'accounts' AS TableName, COUNT(*) AS RowCount FROM accounts
            UNION ALL SELECT 'account_balances', COUNT(*) FROM account_balances
            UNION ALL SELECT 'portfolios', COUNT(*) FROM portfolios
            UNION ALL SELECT 'portfolio_snapshots', COUNT(*) FROM portfolio_snapshots
            UNION ALL SELECT 'portfolio_positions', COUNT(*) FROM portfolio_positions
            UNION ALL SELECT 'transactions', COUNT(*) FROM transactions
            UNION ALL SELECT 'sync_log', COUNT(*) FROM sync_log;
            """;

        var rows = await connection.QueryAsync<(string TableName, long RowCount)>(sql);
        return rows.ToDictionary(r => r.TableName, r => r.RowCount);
    }

    public async Task<IReadOnlyList<(string DataKind, string Status, string? ErrorMessage)>> GetRecentSyncLogAsync(
        CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT data_kind AS DataKind, status AS Status, error_message AS ErrorMessage
            FROM sync_log
            ORDER BY id DESC
            LIMIT 10;
            """;

        var rows = await connection.QueryAsync<(string DataKind, string Status, string? ErrorMessage)>(sql);
        return rows.AsList();
    }
}
