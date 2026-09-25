using Dapper;
using ComdirectFetch.Domain;

namespace ComdirectFetch.Data;

/// <summary>Stammdaten-Zugriff für accounts (KONZEPT.md Abschnitt 5). Upsert, da Konten sich ändern können.</summary>
public sealed class AccountRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<long> UpsertAsync(Account account, CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            INSERT INTO accounts (comdirect_account_id, iban, account_type, display_name, currency)
            VALUES (@ComdirectAccountId, @Iban, @AccountType, @DisplayName, @Currency)
            ON DUPLICATE KEY UPDATE
                iban = VALUES(iban),
                account_type = VALUES(account_type),
                display_name = VALUES(display_name),
                currency = VALUES(currency),
                id = LAST_INSERT_ID(id);
            SELECT LAST_INSERT_ID();
            """;

        return await connection.ExecuteScalarAsync<long>(sql, account);
    }

    public async Task<IReadOnlyList<Account>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT id AS Id, comdirect_account_id AS ComdirectAccountId, iban AS Iban,
                   account_type AS AccountType, display_name AS DisplayName, currency AS Currency
            FROM accounts;
            """;

        var rows = await connection.QueryAsync<Account>(sql);
        return rows.AsList();
    }
}
