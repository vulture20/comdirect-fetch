using ComdirectFetch.Data;
using ComdirectFetch.Domain;

namespace ComdirectFetch.Worker.Services;

/// <summary>
/// Wendet die in KONZEPT.md Abschnitt 6 skizzierte Kategorisierungslogik an: interne
/// Umbuchung → strukturiertes Feld → Freitext-Muster → Vorzeichen-Fallback.
/// <see cref="CategorizeNewTransactionsAsync"/> läuft nur auf frisch gespeicherten,
/// noch unkategorisierten Umsätzen; <see cref="RecategorizeAllAsync"/> wendet den
/// aktuellen Regelsatz erneut auf den gesamten (nicht manuell korrigierten) Bestand an,
/// z. B. nach einer Regel-Erweiterung. Manuell zugeordnete Kategorien werden in beiden
/// Fällen nie angefasst, da sie in den zugrunde liegenden Queries gar nicht erst auftauchen.
/// </summary>
public sealed class CategorizationService(
    AccountRepository accountRepository,
    CategoryRepository categoryRepository,
    CategorizationRuleRepository ruleRepository,
    TransactionRepository transactionRepository,
    ILogger<CategorizationService> logger)
{
    public async Task CategorizeNewTransactionsAsync(CancellationToken cancellationToken = default)
    {
        var uncategorized = await transactionRepository.GetUncategorizedAsync(cancellationToken);
        var (total, updated) = await ApplyCategorizationAsync(uncategorized, cancellationToken);
        if (total > 0)
        {
            logger.LogInformation("{Updated}/{Total} neue Kontoumsätze automatisch kategorisiert.", updated, total);
        }
    }

    /// <summary>
    /// Wendet die Kategorisierungslogik erneut auf alle nicht manuell kategorisierten Umsätze
    /// an (auch bereits automatisch kategorisierte) – für den Fall, dass Regeln erweitert oder
    /// korrigiert wurden (KONZEPT.md Abschnitt 6/9). Manuell zugeordnete Kategorien bleiben
    /// unangetastet, da GetAllNonManuallyCategorizedAsync sie gar nicht erst liefert.
    /// </summary>
    public async Task<(int Total, int Updated)> RecategorizeAllAsync(CancellationToken cancellationToken = default)
    {
        var candidates = await transactionRepository.GetAllNonManuallyCategorizedAsync(cancellationToken);
        var (total, updated) = await ApplyCategorizationAsync(candidates, cancellationToken);
        logger.LogInformation("Neu-Kategorisierung: {Updated}/{Total} Kontoumsätze aktualisiert.", updated, total);
        return (total, updated);
    }

    private async Task<(int Total, int Updated)> ApplyCategorizationAsync(
        IReadOnlyList<Transaction> transactions, CancellationToken cancellationToken)
    {
        if (transactions.Count == 0)
        {
            return (0, 0);
        }

        var accounts = await accountRepository.GetAllAsync(cancellationToken);
        var ownIbans = accounts
            .Where(a => !string.IsNullOrWhiteSpace(a.Iban))
            .Select(a => a.Iban!)
            .ToArray();

        var categoriesByName = (await categoryRepository.GetAllAsync(cancellationToken))
            .ToDictionary(c => c.Name, c => c);
        var rules = await ruleRepository.GetAllOrderedByPriorityAsync(cancellationToken);

        categoriesByName.TryGetValue("Intern/Neutral", out var internalCategory);
        categoriesByName.TryGetValue("Sonstige Einnahme", out var fallbackIncome);
        categoriesByName.TryGetValue("Sonstige Ausgabe", out var fallbackExpense);

        var updated = 0;
        foreach (var transaction in transactions)
        {
            var categoryId = CategorizationLogic.Categorize(
                transaction, ownIbans, internalCategory?.Id, rules, fallbackIncome?.Id, fallbackExpense?.Id);
            if (categoryId is null)
            {
                continue;
            }

            await transactionRepository.UpdateCategoryAsync(transaction.Id, categoryId, manuallyCategorized: false, cancellationToken);
            updated++;
        }

        return (transactions.Count, updated);
    }
}
