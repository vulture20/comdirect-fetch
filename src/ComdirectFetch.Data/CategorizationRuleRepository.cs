using Dapper;
using ComdirectFetch.Domain;

namespace ComdirectFetch.Data;

/// <summary>
/// Zugriff auf categorization_rules (KONZEPT.md Abschnitt 6). Regeln sind Daten, keine
/// Programmlogik, und werden nach aufsteigender Priorität geprüft (erste Übereinstimmung gewinnt).
/// </summary>
public sealed class CategorizationRuleRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyList<CategorizationRule>> GetAllOrderedByPriorityAsync(
        CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT id AS Id, pattern AS Pattern, match_field AS MatchField,
                   category_id AS CategoryId, priority AS Priority
            FROM categorization_rules
            ORDER BY priority ASC;
            """;

        var rows = await connection.QueryAsync<CategorizationRule>(sql);
        return rows.AsList();
    }
}
