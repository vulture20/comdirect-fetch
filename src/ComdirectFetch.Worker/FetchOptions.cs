namespace ComdirectFetch.Worker;

/// <summary>
/// Abruf-Intervalle (KONZEPT.md Abschnitt 4, Kategorie "Abruf-Steuerung"). Der Token-Refresh
/// läuft unabhängig davon deutlich häufiger (Abschnitt 3: alle ca. 8–9 Minuten).
/// </summary>
public sealed class FetchOptions
{
    public const string SectionName = "Fetch";

    public int TokenRefreshIntervalSeconds { get; set; } = 480;
    public int BalancesIntervalSeconds { get; set; } = 900;
    public int PortfolioIntervalSeconds { get; set; } = 3600;
    public int TransactionsIntervalSeconds { get; set; } = 900;
}
