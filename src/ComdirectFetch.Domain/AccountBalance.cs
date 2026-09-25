namespace ComdirectFetch.Domain;

/// <summary>Ein Zeitreihen-Eintrag des Kontosaldos (KONZEPT.md Abschnitt 5, Tabelle account_balances).</summary>
public sealed class AccountBalance
{
    public long Id { get; set; }
    public required long AccountId { get; set; }
    public required DateTimeOffset Timestamp { get; set; }
    public required decimal Balance { get; set; }
    public required decimal AvailableAmount { get; set; }
    public required string Currency { get; set; }
}
