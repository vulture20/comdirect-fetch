using Dapper;
using ComdirectFetch.Domain;

namespace ComdirectFetch.Data;

/// <summary>
/// Bewusst eine Klasse mit Property-Settern statt eines positional-Records: Dapper materialisiert
/// Records über deren Primärkonstruktor und verlangt dafür exakte CLR-Typ-Übereinstimmung mit der
/// SQL-Spalte (hier DATETIME -&gt; <see cref="DateTime"/>) - die sonst überall in diesem Projekt
/// klaglos funktionierende automatische DateTime-zu-DateTimeOffset-Konvertierung greift nur beim
/// property-setter-basierten Materialisieren normaler Klassen, live bestätigt durch
/// "A parameterless default constructor or one matching signature ... is required" beim ersten
/// Einsatz dieses Records (Issue #12).
/// </summary>
public sealed class PortfolioValuationPoint
{
    public DateTimeOffset Timestamp { get; set; }
    public decimal TotalValue { get; set; }
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
    /// bereits eingefügten Snapshot nach - separat von InsertSnapshotAsync, da DepotPerformanceCalculator
    /// die komplette Historie inkl. des gerade eingefügten Snapshots als Eingabe braucht.</summary>
    public async Task UpdatePerformanceAsync(
        long snapshotId,
        decimal? netInvestedCapital,
        decimal? dividendsReceived,
        decimal? timeWeightedReturnPercent,
        CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            UPDATE portfolio_snapshots
            SET net_invested_capital = @NetInvestedCapital,
                dividends_received = @DividendsReceived,
                time_weighted_return_pct = @TimeWeightedReturnPercent
            WHERE id = @SnapshotId;
            """;

        await connection.ExecuteAsync(sql, new
        {
            SnapshotId = snapshotId,
            NetInvestedCapital = netInvestedCapital,
            DividendsReceived = dividendsReceived,
            TimeWeightedReturnPercent = timeWeightedReturnPercent,
        });
    }

    /// <summary>Zeitpunkt+Depotwert aller Snapshots eines Depots, aufsteigend - Grundlage für
    /// DepotPerformanceCalculator.ConsolidateToDailyLastValue (Issue #12). Eigener Record statt
    /// ValueTuple, da Dapper QueryAsync&lt;T&gt; ValueTuples nicht direkt mappen kann.</summary>
    public async Task<IReadOnlyList<PortfolioValuationPoint>> GetValuationHistoryAsync(
        long portfolioId, CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT recorded_at AS Timestamp, total_value AS TotalValue
            FROM portfolio_snapshots
            WHERE portfolio_id = @PortfolioId
            ORDER BY recorded_at ASC;
            """;

        var rows = await connection.QueryAsync<PortfolioValuationPoint>(sql, new { PortfolioId = portfolioId });
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
