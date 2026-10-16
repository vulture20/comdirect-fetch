namespace ComdirectFetch.Domain;

/// <summary>
/// Handelsgebühr eines Trades (Issue #19, KONZEPT.md Abschnitt 17). Reine, DB-unabhängige Funktion
/// (gleiches Muster wie <see cref="CategorizationLogic"/>).
///
/// comdirect weist Gebühren weder als eigene Kontobuchung noch als Feld der Depot-Transaktion aus. Live
/// geprüft: <c>transactionValue</c> ist der Abrechnungsbetrag inklusive Gebühren (centgenau gleich der
/// Kontobuchung), <c>executionPrice</c> der reine Ausführungskurs. Die Gebühr ist daher die Differenz
/// zum Kurswert (Stück x Kurs): beim Kauf zahlt man mehr als den Kurswert, beim Verkauf bekommt man
/// weniger. Der Spread steckt im Ausführungskurs und ist damit bewusst <em>nicht</em> enthalten.
/// Rundungsrauschen aus der Kursdarstellung liegt live unter einem halben Cent und verschwindet durch das
/// Runden auf Cent.
/// </summary>
public static class TradeFeeCalculator
{
    /// <returns>Gebühr in der Abrechnungswährung, auf Cent gerundet; null, wenn Richtung, Kurs oder
    /// Abrechnungsbetrag fehlen bzw. die Richtung unbekannt ist.</returns>
    public static decimal? Calculate(string? direction, decimal quantity, decimal? executionPrice, decimal? transactionValue)
    {
        if (executionPrice is not { } price || transactionValue is not { } value)
        {
            return null;
        }

        var marketValue = quantity * price;
        decimal? fee = direction switch
        {
            "IN" => value - marketValue,
            "OUT" => marketValue - value,
            _ => null,
        };

        return fee is { } f ? Math.Round(f, 2, MidpointRounding.AwayFromZero) : null;
    }
}
