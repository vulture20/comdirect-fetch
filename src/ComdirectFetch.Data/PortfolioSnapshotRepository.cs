using Dapper;
using ComdirectFetch.Domain;

namespace ComdirectFetch.Data;

/// <summary>Zeitreihen-Zugriff für portfolio_snapshots und portfolio_positions (KONZEPT.md Abschnitt 5).</summary>
public sealed class PortfolioSnapshotRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<long> InsertSnapshotAsync(PortfolioSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            INSERT INTO portfolio_snapshots (portfolio_id, recorded_at, total_value, acquisition_value, currency)
            VALUES (@PortfolioId, @Timestamp, @TotalValue, @AcquisitionValue, @Currency);
            SELECT LAST_INSERT_ID();
            """;

        return await connection.ExecuteScalarAsync<long>(sql, snapshot);
    }

    public async Task InsertPositionsAsync(
        IEnumerable<PortfolioPosition> positions,
        CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            INSERT INTO portfolio_positions
                (snapshot_id, isin, wkn, instrument_type, display_name, quantity, market_value, acquisition_value, profit_loss, currency)
            VALUES
                (@SnapshotId, @Isin, @Wkn, @InstrumentType, @DisplayName, @Quantity, @MarketValue, @AcquisitionValue, @ProfitLoss, @Currency);
            """;

        await connection.ExecuteAsync(sql, positions);
    }
}
