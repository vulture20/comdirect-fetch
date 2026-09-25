namespace ComdirectFetch.Domain;

/// <summary>Eine Einzelposition innerhalb eines Depot-Snapshots (KONZEPT.md Abschnitt 5, Tabelle portfolio_positions).</summary>
public sealed class PortfolioPosition
{
    public long Id { get; set; }
    public required long SnapshotId { get; set; }
    public string? Isin { get; set; }
    public string? Wkn { get; set; }
    public required string DisplayName { get; set; }
    public required decimal Quantity { get; set; }
    public required decimal MarketValue { get; set; }
    public required decimal AcquisitionValue { get; set; }
    public required decimal ProfitLoss { get; set; }
    public required string Currency { get; set; }
}
