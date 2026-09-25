namespace ComdirectFetch.Domain;

/// <summary>Stammdaten eines comdirect-Depots (KONZEPT.md Abschnitt 5, Tabelle portfolios).</summary>
public sealed class Portfolio
{
    public long Id { get; set; }
    public required string ComdirectPortfolioId { get; set; }
    public required string DisplayName { get; set; }
}
