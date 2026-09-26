using Dapper;
using ComdirectFetch.Domain;

namespace ComdirectFetch.Data;

/// <summary>Zugriff auf categories (KONZEPT.md Abschnitt 6, Stammdaten/Konfiguration).</summary>
public sealed class CategoryRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyList<Category>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = "SELECT id AS Id, name AS Name, type AS Type FROM categories;";
        var rows = await connection.QueryAsync<Category>(sql);
        return rows.AsList();
    }

    public async Task<Category?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = "SELECT id AS Id, name AS Name, type AS Type FROM categories WHERE id = @Id;";
        return await connection.QueryFirstOrDefaultAsync<Category>(sql, new { Id = id });
    }

    /// <summary>Zugriffsschicht für die neue Kategorien-Verwaltung (GitHub-Issue #13, KONZEPT.md Abschnitt 12).</summary>
    public async Task<long> CreateAsync(Category category, CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            INSERT INTO categories (name, type) VALUES (@Name, @Type);
            SELECT LAST_INSERT_ID();
            """;

        // Enum explizit als String übergeben (Dapper-Parameter-Gotcha, siehe SyncLogRepository).
        return await connection.ExecuteScalarAsync<long>(sql, new { category.Name, Type = category.Type.ToString() });
    }

    public async Task UpdateAsync(Category category, CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = "UPDATE categories SET name = @Name, type = @Type WHERE id = @Id;";
        await connection.ExecuteAsync(sql, new { category.Id, category.Name, Type = category.Type.ToString() });
    }

    /// <summary>Wirft bei Fremdschlüssel-Konflikt (Kategorie wird noch von Regeln/Umsätzen referenziert) die DB-Ausnahme unverändert weiter - vom Aufrufer zu behandeln.</summary>
    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = "DELETE FROM categories WHERE id = @Id;";
        await connection.ExecuteAsync(sql, new { Id = id });
    }
}
