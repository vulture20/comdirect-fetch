namespace ComdirectFetch.Domain;

/// <summary>Ein Kauf/Verkauf im Depot laut comdirect (Issue #19, KONZEPT.md Abschnitt 17, Tabelle depot_transactions).</summary>
public sealed class DepotTransaction
{
    public long Id { get; set; }
    public required long PortfolioId { get; set; }
    public required string ComdirectTransactionId { get; set; }

    /// <summary>comdirect-Buchungsstatus, z. B. "BOOKED".</summary>
    public required string BookingStatus { get; set; }

    /// <summary>Buchungstag im Depot (live: ein bis mehrere Tage nach dem Handelstag).</summary>
    public DateOnly? BookingDate { get; set; }

    /// <summary>Handelstag (Ausführung).</summary>
    public DateOnly? BusinessDate { get; set; }

    /// <summary>comdirect-transactionType, z. B. "BUY"/"SELL".</summary>
    public string? TransactionType { get; set; }

    /// <summary>comdirect-transactionDirection: "IN" (Zugang, Kauf) oder "OUT" (Abgang, Verkauf).</summary>
    public string? Direction { get; set; }

    public string? Isin { get; set; }
    public string? Wkn { get; set; }
    public string? InstrumentName { get; set; }
    public string? InstrumentType { get; set; }
    public required decimal Quantity { get; set; }
    public decimal? ExecutionPrice { get; set; }

    /// <summary>Abrechnungsbetrag inkl. Gebühren (live geprüft: centgenau gleich der Kontobuchung).</summary>
    public decimal? TransactionValue { get; set; }

    public string? Currency { get; set; }

    /// <summary>Gebühr laut <see cref="TradeFeeCalculator"/>; null, wenn nicht berechenbar.</summary>
    public decimal? FeeAmount { get; set; }

    public required DateTimeOffset FirstSeenAt { get; set; }
}
