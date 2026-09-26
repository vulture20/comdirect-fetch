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

    /// <summary>IBAN der Gegenseite (Remitter bei Gutschrift, Debtor/Creditor bei Belastung), falls von
    /// comdirect geliefert. Primäres Signal zur Erkennung interner Umbuchungen (KONZEPT.md Abschnitt 6).</summary>
    public string? CounterpartyIban { get; set; }

    /// <summary>Name der Gegenseite (Remitter bei Gutschrift, Debtor/Creditor bei Belastung), falls von
    /// comdirect geliefert (holderName) - Signal für Kategorisierungsregeln mit
    /// RuleMatchField.CounterpartyName, für Buchungen, deren Empfänger-Name nur strukturiert
    /// vorliegt, nicht im Buchungstext (KONZEPT.md Abschnitt 6).</summary>
    public string? CounterpartyName { get; set; }

    public long? CategoryId { get; set; }
    public bool ManuallyCategorized { get; set; }
    public required DateTimeOffset FirstSeenAt { get; set; }
}
