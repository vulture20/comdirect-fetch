using Dapper;
using ComdirectFetch.Domain;

namespace ComdirectFetch.Data;

/// <summary>
/// Zugriff auf transactions (KONZEPT.md Abschnitt 5). Jede Buchung wird nur einmal
/// gespeichert: INSERT IGNORE auf den eindeutigen Schlüssel (account_id, comdirect_reference)
/// verwirft erneut gelieferte, bereits bekannte Umsätze stillschweigend.
/// </summary>
public sealed class TransactionRepository(IDbConnectionFactory connectionFactory)
{
    /// <returns>true, wenn der Umsatz neu gespeichert wurde; false, wenn er bereits bekannt war.</returns>
    public async Task<bool> InsertIfNewAsync(Transaction transaction, CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            INSERT IGNORE INTO transactions
                (account_id, comdirect_reference, booking_date, value_date, amount, currency,
                 booking_text, transaction_type, category_id, manually_categorized, first_seen_at)
            VALUES
                (@AccountId, @ComdirectReference, @BookingDate, @ValueDate, @Amount, @Currency,
                 @BookingText, @TransactionType, @CategoryId, @ManuallyCategorized, @FirstSeenAt);
            """;

        var affectedRows = await connection.ExecuteAsync(sql, transaction);
        return affectedRows > 0;
    }

    public async Task UpdateCategoryAsync(
        long transactionId,
        long? categoryId,
        bool manuallyCategorized,
        CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            UPDATE transactions
            SET category_id = @CategoryId, manually_categorized = @ManuallyCategorized
            WHERE id = @TransactionId;
            """;

        await connection.ExecuteAsync(sql, new { TransactionId = transactionId, CategoryId = categoryId, ManuallyCategorized = manuallyCategorized });
    }

    /// <summary>Automatisch (nicht manuell) kategorisierte Umsätze ohne Kategorie – Basis für die Regelanwendung.</summary>
    public async Task<IReadOnlyList<Transaction>> GetUncategorizedAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT id AS Id, account_id AS AccountId, comdirect_reference AS ComdirectReference,
                   booking_date AS BookingDate, value_date AS ValueDate, amount AS Amount, currency AS Currency,
                   booking_text AS BookingText, transaction_type AS TransactionType,
                   category_id AS CategoryId, manually_categorized AS ManuallyCategorized, first_seen_at AS FirstSeenAt
            FROM transactions
            WHERE category_id IS NULL AND manually_categorized = 0;
            """;

        var rows = await connection.QueryAsync<Transaction>(sql);
        return rows.AsList();
    }
}
