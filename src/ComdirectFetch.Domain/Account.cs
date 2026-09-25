namespace ComdirectFetch.Domain;

/// <summary>Stammdaten eines comdirect-Kontos (KONZEPT.md Abschnitt 5, Tabelle accounts).</summary>
public sealed class Account
{
    public long Id { get; set; }
    public required string ComdirectAccountId { get; set; }
    public string? Iban { get; set; }
    public required string AccountType { get; set; }
    public required string DisplayName { get; set; }
    public required string Currency { get; set; }
}
