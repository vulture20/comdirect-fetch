using Dapper;
using ComdirectFetch.Domain;

namespace ComdirectFetch.Data;

/// <summary>Zeitreihen-Zugriff für account_balances (KONZEPT.md Abschnitt 5). Immer INSERT, nie UPDATE.</summary>
public sealed class AccountBalanceRepository(IDbConnectionFactory connectionFactory)
{
    public async Task InsertAsync(AccountBalance balance, CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            INSERT INTO account_balances (account_id, recorded_at, balance, available_amount, currency)
            VALUES (@AccountId, @Timestamp, @Balance, @AvailableAmount, @Currency);
            """;

        await connection.ExecuteAsync(sql, balance);
    }

    /// <summary>Der jeweils aktuellste gebuchte Saldo der angegebenen Konten - Ankerpunkt für die Rückrechnung
    /// des Guthabens je Tag in DepotPerformanceCalculator (Issue #12). Konten ohne Saldo fehlen im Ergebnis.</summary>
    public async Task<IReadOnlyDictionary<long, decimal>> GetLatestBalancesAsync(
        IEnumerable<long> accountIds, CancellationToken cancellationToken = default)
    {
        var ids = accountIds.ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<long, decimal>();
        }

        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT b.account_id AS AccountId, b.balance AS Balance
            FROM account_balances b
            WHERE b.account_id IN @AccountIds
              AND b.recorded_at = (SELECT MAX(x.recorded_at) FROM account_balances x WHERE x.account_id = b.account_id);
            """;

        var rows = await connection.QueryAsync<(long AccountId, decimal Balance)>(sql, new { AccountIds = ids });
        return rows.ToDictionary(r => r.AccountId, r => r.Balance);
    }
}
