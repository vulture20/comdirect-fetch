namespace ComdirectFetch.Domain;

/// <summary>Welches Feld eines Umsatzes eine Kategorisierungsregel prüft (KONZEPT.md Abschnitt 6).</summary>
public enum RuleMatchField
{
    BookingText,
    TransactionType,

    /// <summary>Name der Gegenseite (Transaction.CounterpartyName) - für Buchungen, deren Empfänger-Name nur strukturiert vorliegt, nicht im Buchungstext.</summary>
    CounterpartyName
}

/// <summary>
/// Eine priorisierte Muster-Regel zur automatischen Kategorisierung von Kontoumsätzen
/// (KONZEPT.md Abschnitt 6, Tabelle categorization_rules). Regeln sind Daten, keine
/// Programmlogik, und werden nach aufsteigender <see cref="Priority"/> geprüft.
/// </summary>
public sealed class CategorizationRule
{
    public long Id { get; set; }
    public required string Pattern { get; set; }
    public required RuleMatchField MatchField { get; set; }
    public required long CategoryId { get; set; }
    public required int Priority { get; set; }
}
