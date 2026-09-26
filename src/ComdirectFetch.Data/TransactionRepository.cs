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
                 booking_text, transaction_type, counterparty_iban, category_id, manually_categorized, first_seen_at)
            VALUES
                (@AccountId, @ComdirectReference, @BookingDate, @ValueDate, @Amount, @Currency,
                 @BookingText, @TransactionType, @CounterpartyIban, @CategoryId, @ManuallyCategorized, @FirstSeenAt);
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
                   booking_text AS BookingText, transaction_type AS TransactionType, counterparty_iban AS CounterpartyIban,
                   category_id AS CategoryId, manually_categorized AS ManuallyCategorized, first_seen_at AS FirstSeenAt
            FROM transactions
            WHERE category_id IS NULL AND manually_categorized = 0;
            """;

        var rows = await connection.QueryAsync<Transaction>(sql);
        return rows.AsList();
    }

    /// <summary>
    /// Alle automatisch (nicht manuell) kategorisierten Umsätze, unabhängig vom aktuellen
    /// category_id-Wert – Basis für eine erneute Regelanwendung nach Regel-Erweiterung/
    /// -Korrektur (KONZEPT.md Abschnitt 6/9). Im Gegensatz zu GetUncategorizedAsync ohne
    /// "category_id IS NULL"-Filter.
    /// </summary>
    public async Task<IReadOnlyList<Transaction>> GetAllNonManuallyCategorizedAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT id AS Id, account_id AS AccountId, comdirect_reference AS ComdirectReference,
                   booking_date AS BookingDate, value_date AS ValueDate, amount AS Amount, currency AS Currency,
                   booking_text AS BookingText, transaction_type AS TransactionType, counterparty_iban AS CounterpartyIban,
                   category_id AS CategoryId, manually_categorized AS ManuallyCategorized, first_seen_at AS FirstSeenAt
            FROM transactions
            WHERE manually_categorized = 0;
            """;

        var rows = await connection.QueryAsync<Transaction>(sql);
        return rows.AsList();
    }

    /// <summary>Alle Umsätze unabhängig vom Kategorisierungsstatus - Basis für die Regel-Vorschau gegen Echtdaten (GitHub-Issue #13, KONZEPT.md Abschnitt 12).</summary>
    public async Task<IReadOnlyList<Transaction>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT id AS Id, account_id AS AccountId, comdirect_reference AS ComdirectReference,
                   booking_date AS BookingDate, value_date AS ValueDate, amount AS Amount, currency AS Currency,
                   booking_text AS BookingText, transaction_type AS TransactionType, counterparty_iban AS CounterpartyIban,
                   category_id AS CategoryId, manually_categorized AS ManuallyCategorized, first_seen_at AS FirstSeenAt
            FROM transactions;
            """;

        var rows = await connection.QueryAsync<Transaction>(sql);
        return rows.AsList();
    }
}
