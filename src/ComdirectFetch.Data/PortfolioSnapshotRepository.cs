using Dapper;
using ComdirectFetch.Domain;

namespace ComdirectFetch.Data;

/// <summary>
/// Ein Snapshot samt der bereits gespeicherten Performance-Werte (Issue #12). Bewusst eine Klasse mit
/// Property-Settern statt eines positional-Records: Dapper materialisiert Records über deren
/// Primärkonstruktor und verlangt dafür exakte CLR-Typ-Übereinstimmung mit der SQL-Spalte (hier
/// DATETIME -&gt; <see cref="DateTime"/>) - die sonst überall in diesem Projekt klaglos funktionierende
/// automatische DateTime-zu-DateTimeOffset-Konvertierung greift nur beim property-setter-basierten
/// Materialisieren normaler Klassen, live bestätigt durch "A parameterless default constructor or one
/// matching signature ... is required" beim ersten Einsatz eines Records an dieser Stelle.
/// </summary>
public sealed class PortfolioSnapshotPerformanceRow
{
    public long SnapshotId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public decimal PositionsValue { get; set; }
    public decimal? SettlementCash { get; set; }
    public decimal? NetInvestedCapital { get; set; }
    public decimal? DividendsReceived { get; set; }
    public decimal? TimeWeightedReturnPercent { get; set; }
}

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

    /// <summary>Trägt die um externe Ein-/Auszahlungen bereinigten Kennzahlen (Issue #12) für einen
    /// Snapshot nach - separat von InsertSnapshotAsync, da DepotPerformanceCalculator die komplette
    /// Historie als Eingabe braucht.</summary>
    public async Task UpdatePerformanceAsync(
        long snapshotId,
        decimal settlementCash,
        decimal netInvestedCapital,
        decimal dividendsReceived,
        decimal? timeWeightedReturnPercent,
        CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            UPDATE portfolio_snapshots
            SET settlement_cash = @SettlementCash,
                net_invested_capital = @NetInvestedCapital,
                dividends_received = @DividendsReceived,
                time_weighted_return_pct = @TimeWeightedReturnPercent
            WHERE id = @SnapshotId;
            """;

        await connection.ExecuteAsync(sql, new
        {
            SnapshotId = snapshotId,
            SettlementCash = settlementCash,
            NetInvestedCapital = netInvestedCapital,
            DividendsReceived = dividendsReceived,
            TimeWeightedReturnPercent = timeWeightedReturnPercent,
        });
    }

    /// <summary>Alle Snapshots eines Depots mit den bereits gespeicherten Performance-Werten, aufsteigend -
    /// Eingabe für DepotPerformanceCalculator.Calculate und Vergleichsbasis, um nur geänderte Zeilen zu schreiben.</summary>
    public async Task<IReadOnlyList<PortfolioSnapshotPerformanceRow>> GetPerformanceRowsAsync(
        long portfolioId, CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT id AS SnapshotId, recorded_at AS Timestamp, total_value AS PositionsValue,
                   settlement_cash AS SettlementCash,
                   net_invested_capital AS NetInvestedCapital, dividends_received AS DividendsReceived,
                   time_weighted_return_pct AS TimeWeightedReturnPercent
            FROM portfolio_snapshots
            WHERE portfolio_id = @PortfolioId
            ORDER BY recorded_at ASC;
            """;

        var rows = await connection.QueryAsync<PortfolioSnapshotPerformanceRow>(sql, new { PortfolioId = portfolioId });
        return rows.AsList();
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
