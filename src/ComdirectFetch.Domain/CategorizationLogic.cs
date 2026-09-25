namespace ComdirectFetch.Domain;

/// <summary>
/// Reine, DB-unabhängige Kategorisierungslogik (KONZEPT.md Abschnitt 6): interne Umbuchung →
/// strukturiertes Feld/Freitext-Muster (priorisierte Regelliste) → Vorzeichen-Fallback.
/// Bewusst als reine Funktion gehalten, damit sie ohne Datenbank testbar ist.
/// </summary>
public static class CategorizationLogic
{
    public static long? Categorize(
        Transaction transaction,
        IReadOnlyCollection<string> ownIbans,
        long? internalCategoryId,
        IReadOnlyList<CategorizationRule> rulesByPriority,
        long? fallbackIncomeCategoryId,
        long? fallbackExpenseCategoryId)
    {
        if (transaction.BookingText is not null
            && ownIbans.Any(iban => transaction.BookingText.Contains(iban, StringComparison.OrdinalIgnoreCase)))
        {
            return internalCategoryId;
        }

        foreach (var rule in rulesByPriority)
        {
            var haystack = rule.MatchField == RuleMatchField.TransactionType
                ? transaction.TransactionType
                : transaction.BookingText;

            if (haystack is not null && haystack.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase))
            {
                return rule.CategoryId;
            }
        }

        return transaction.Amount >= 0 ? fallbackIncomeCategoryId : fallbackExpenseCategoryId;
    }
}
