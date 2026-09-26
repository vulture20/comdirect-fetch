namespace ComdirectFetch.Domain;

/// <summary>
/// Kategorienamen, die im Code fest verankert sind (KONZEPT.md Abschnitt 6/12, GitHub-Issue
/// #13): <see cref="CategorizationLogic"/>/<c>CategorizationService</c> suchen sie namentlich
/// für den Vorzeichen-Fallback und die interne-Umbuchung-Erkennung. Löschen oder Umbenennen
/// würde diese Logik stillschweigend brechen - die neue Kategorien-/Regel-Verwaltung lehnt
/// beides für diese Namen serverseitig ab. Zentral gepflegt, damit Prüfung (Endpunkte) und
/// Nutzung (CategorizationService) nicht auseinanderlaufen.
/// </summary>
public static class ProtectedCategoryNames
{
    public const string Internal = "Intern/Neutral";
    public const string FallbackIncome = "Sonstige Einnahme";
    public const string FallbackExpense = "Sonstige Ausgabe";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Internal,
        FallbackIncome,
        FallbackExpense,
    };
}
