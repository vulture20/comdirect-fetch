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

    /// <summary>Netto-Kapitaleinsatz-Methode (Issue #12) - null für Snapshots vor v0.23.0 oder ohne
    /// erkannte Depot↔Verrechnungskonto-Verknüpfung; wird nachträglich nie befüllt (kein Backfill).</summary>
    public decimal? NetInvestedCapital { get; set; }
    public decimal? DividendsReceived { get; set; }
    public decimal? TimeWeightedReturnPercent { get; set; }
}
