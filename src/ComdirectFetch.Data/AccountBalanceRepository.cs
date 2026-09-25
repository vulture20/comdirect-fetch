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
}
