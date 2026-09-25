using ComdirectFetch.Data;
using ComdirectFetch.Domain;

namespace ComdirectFetch.Worker.Services;

/// <summary>
/// Wendet die in KONZEPT.md Abschnitt 6 skizzierte Kategorisierungslogik auf neu
/// gespeicherte, noch unkategorisierte Kontoumsätze an: interne Umbuchung → strukturiertes
/// Feld → Freitext-Muster → Vorzeichen-Fallback. Manuell zugeordnete Kategorien werden nie
/// angefasst (sie tauchen in GetUncategorizedAsync gar nicht erst auf).
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
        if (uncategorized.Count == 0)
        {
            return;
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
        foreach (var transaction in uncategorized)
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

        logger.LogInformation("{Count} Kontoumsätze automatisch kategorisiert.", updated);
    }
}
