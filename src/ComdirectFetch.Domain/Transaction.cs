namespace ComdirectFetch.Domain;

/// <summary>
/// Ein Kontoumsatz. Wird je Buchung genau einmal gespeichert (Dedup über
/// <see cref="ComdirectReference"/>), siehe KONZEPT.md Abschnitt 5, Tabelle transactions.
/// </summary>
public sealed class Transaction
{
    public long Id { get; set; }
    public required long AccountId { get; set; }
    public required string ComdirectReference { get; set; }
    public required DateOnly BookingDate { get; set; }
    public DateOnly? ValueDate { get; set; }
    public required decimal Amount { get; set; }
    public required string Currency { get; set; }
    public string? BookingText { get; set; }
    public string? TransactionType { get; set; }
    public long? CategoryId { get; set; }
    public bool ManuallyCategorized { get; set; }
    public required DateTimeOffset FirstSeenAt { get; set; }
}
