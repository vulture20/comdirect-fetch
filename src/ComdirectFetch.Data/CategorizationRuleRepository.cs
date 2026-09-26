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

    public async Task<CategorizationRule?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT id AS Id, pattern AS Pattern, match_field AS MatchField,
                   category_id AS CategoryId, priority AS Priority
            FROM categorization_rules
            WHERE id = @Id;
            """;

        return await connection.QueryFirstOrDefaultAsync<CategorizationRule>(sql, new { Id = id });
    }

    /// <summary>Zugriffsschicht für die neue Regel-Verwaltung (GitHub-Issue #13, KONZEPT.md Abschnitt 12).</summary>
    public async Task<long> CreateAsync(CategorizationRule rule, CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            INSERT INTO categorization_rules (pattern, match_field, category_id, priority)
            VALUES (@Pattern, @MatchField, @CategoryId, @Priority);
            SELECT LAST_INSERT_ID();
            """;

        // Enum explizit als String übergeben (Dapper-Parameter-Gotcha, siehe SyncLogRepository).
        return await connection.ExecuteScalarAsync<long>(sql, new
        {
            rule.Pattern,
            MatchField = rule.MatchField.ToString(),
            rule.CategoryId,
            rule.Priority,
        });
    }

    public async Task UpdateAsync(CategorizationRule rule, CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            UPDATE categorization_rules
            SET pattern = @Pattern, match_field = @MatchField, category_id = @CategoryId, priority = @Priority
            WHERE id = @Id;
            """;

        await connection.ExecuteAsync(sql, new
        {
            rule.Id,
            rule.Pattern,
            MatchField = rule.MatchField.ToString(),
            rule.CategoryId,
            rule.Priority,
        });
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = "DELETE FROM categorization_rules WHERE id = @Id;";
        await connection.ExecuteAsync(sql, new { Id = id });
    }

    /// <summary>
    /// Für die "weiche" Prioritäts-Kollisionswarnung beim Anlegen/Ändern einer Regel (KONZEPT.md
    /// Abschnitt 12) - kein Hard-Block, da eine Kollision nicht zwangsläufig falsch ist, nur
    /// mehrdeutig (Reihenfolge zwischen zwei Regeln gleicher Priorität ist sonst undefiniert).
    /// </summary>
    public async Task<bool> PriorityInUseAsync(int priority, long? excludeId, CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT COUNT(*) FROM categorization_rules
            WHERE priority = @Priority AND (@ExcludeId IS NULL OR id <> @ExcludeId);
            """;

        var count = await connection.ExecuteScalarAsync<long>(sql, new { Priority = priority, ExcludeId = excludeId });
        return count > 0;
    }
}
