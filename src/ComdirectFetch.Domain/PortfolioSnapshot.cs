namespace ComdirectFetch.Domain;

/// <summary>Ein Zeitreihen-Eintrag der Depotübersicht (KONZEPT.md Abschnitt 5, Tabelle portfolio_snapshots).</summary>
public sealed class PortfolioSnapshot
{
    public long Id { get; set; }
    public required long PortfolioId { get; set; }
    public required DateTimeOffset Timestamp { get; set; }
    public required decimal TotalValue { get; set; }
    public required decimal AcquisitionValue { get; set; }
    public required string Currency { get; set; }
}
