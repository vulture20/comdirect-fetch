using Dapper;

namespace ComdirectFetch.Data;

/// <summary>
/// Zugriff auf portfolio_settlement_accounts (Issue #12, KONZEPT.md Abschnitt 6 Phase 4) - welche
/// Konten mit einem Depot als Verrechnungskonto verknüpft sind, Grundlage für die um externe
/// Ein-/Auszahlungen bereinigte Performance-Berechnung.
/// </summary>
public sealed class PortfolioSettlementAccountRepository(IDbConnectionFactory connectionFactory)
{
    /// <summary>Ersetzt die komplette Verknüpfungsliste eines Depots - einfacher und robuster gegen
    /// comdirect-seitige Änderungen (z. B. ein neu hinzugefügtes Verrechnungskonto) als ein Diff.</summary>
    public async Task ReplaceForPortfolioAsync(
        long portfolioId,
        IEnumerable<(long AccountId, bool IsDefault)> accounts,
        CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(
            "DELETE FROM portfolio_settlement_accounts WHERE portfolio_id = @PortfolioId;",
            new { PortfolioId = portfolioId });

        const string insertSql = """
            INSERT INTO portfolio_settlement_accounts (portfolio_id, account_id, is_default)
            VALUES (@PortfolioId, @AccountId, @IsDefault);
            """;
        foreach (var (accountId, isDefault) in accounts)
        {
            await connection.ExecuteAsync(insertSql, new { PortfolioId = portfolioId, AccountId = accountId, IsDefault = isDefault });
        }
    }

    public async Task<IReadOnlyList<PortfolioSettlementAccountLink>> GetLinksAsync(
        long portfolioId, CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<PortfolioSettlementAccountLink>(
            "SELECT account_id AS AccountId, is_default AS IsDefault FROM portfolio_settlement_accounts WHERE portfolio_id = @PortfolioId;",
            new { PortfolioId = portfolioId });
        return rows.AsList();
    }
}

/// <summary>Ein verknüpftes Konto eines Depots; IsDefault markiert das Standard-Verrechnungskonto (defaultSettlementAccountId).</summary>
public sealed class PortfolioSettlementAccountLink
{
    public long AccountId { get; set; }
    public bool IsDefault { get; set; }
}
