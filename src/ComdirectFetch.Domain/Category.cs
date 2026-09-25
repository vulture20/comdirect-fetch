namespace ComdirectFetch.Domain;

/// <summary>Eine Kategorie für die Cashflow-Analyse/Kostenübersicht (KONZEPT.md Abschnitt 6, Tabelle categories).</summary>
public sealed class Category
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public required CategoryType Type { get; set; }
}
