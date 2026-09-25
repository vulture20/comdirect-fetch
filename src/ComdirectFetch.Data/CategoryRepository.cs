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
}
