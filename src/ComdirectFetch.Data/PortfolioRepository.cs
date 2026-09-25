using Dapper;
using ComdirectFetch.Domain;

namespace ComdirectFetch.Data;

/// <summary>Stammdaten-Zugriff für portfolios (KONZEPT.md Abschnitt 5). Upsert, da Depots sich ändern können.</summary>
public sealed class PortfolioRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<long> UpsertAsync(Portfolio portfolio, CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            INSERT INTO portfolios (comdirect_portfolio_id, display_name)
            VALUES (@ComdirectPortfolioId, @DisplayName)
            ON DUPLICATE KEY UPDATE
                display_name = VALUES(display_name),
                id = LAST_INSERT_ID(id);
            SELECT LAST_INSERT_ID();
            """;

        return await connection.ExecuteScalarAsync<long>(sql, portfolio);
    }

    public async Task<IReadOnlyList<Portfolio>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT id AS Id, comdirect_portfolio_id AS ComdirectPortfolioId, display_name AS DisplayName
            FROM portfolios;
            """;

        var rows = await connection.QueryAsync<Portfolio>(sql);
        return rows.AsList();
    }
}
