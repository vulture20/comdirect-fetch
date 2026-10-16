using ComdirectFetch.Domain;
using Dapper;

namespace ComdirectFetch.Data;

/// <summary>Zugriff auf depot_transactions (Issue #19, KONZEPT.md Abschnitt 17).</summary>
public sealed class DepotTransactionRepository(IDbConnectionFactory connectionFactory)
{
    /// <summary>
    /// Fügt eine Depot-Transaktion ein oder aktualisiert sie (Dedup über die comdirect-transactionId).
    /// Anders als bei den Kontoumsätzen gibt es hier keine manuell gepflegten Spalten, die geschützt
    /// werden müssten – deshalb werden alle Werte überschrieben, damit z. B. ein Statuswechsel oder eine
    /// geänderte Gebührenberechnung beim nächsten Abruf automatisch nachgezogen wird. first_seen_at
    /// bleibt erhalten.
    /// </summary>
    /// <returns>true, wenn die Transaktion neu gespeichert wurde.</returns>
    public async Task<bool> UpsertAsync(DepotTransaction transaction, CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            INSERT INTO depot_transactions
                (portfolio_id, comdirect_transaction_id, booking_status, booking_date, business_date,
                 transaction_type, direction, isin, wkn, instrument_name, instrument_type, quantity,
                 execution_price, transaction_value, currency, fee_amount, first_seen_at)
            VALUES
                (@PortfolioId, @ComdirectTransactionId, @BookingStatus, @BookingDate, @BusinessDate,
                 @TransactionType, @Direction, @Isin, @Wkn, @InstrumentName, @InstrumentType, @Quantity,
                 @ExecutionPrice, @TransactionValue, @Currency, @FeeAmount, @FirstSeenAt)
            ON DUPLICATE KEY UPDATE
                portfolio_id = VALUES(portfolio_id),
                booking_status = VALUES(booking_status),
                booking_date = VALUES(booking_date),
                business_date = VALUES(business_date),
                transaction_type = VALUES(transaction_type),
                direction = VALUES(direction),
                isin = VALUES(isin),
                wkn = VALUES(wkn),
                instrument_name = VALUES(instrument_name),
                instrument_type = VALUES(instrument_type),
                quantity = VALUES(quantity),
                execution_price = VALUES(execution_price),
                transaction_value = VALUES(transaction_value),
                currency = VALUES(currency),
                fee_amount = VALUES(fee_amount);
            """;

        // Bewusst eine Existenzprüfung statt der betroffenen Zeilen: MySqlConnector meldet standardmäßig
        // gefundene statt geänderte Zeilen (UseAffectedRows=false), ein unverändertes Duplikat zählt dann
        // ebenfalls als 1 (live beobachtet: jeder Lauf meldete alle Transaktionen als neu).
        var exists = await connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM depot_transactions WHERE comdirect_transaction_id = @ComdirectTransactionId);",
            transaction);
        await connection.ExecuteAsync(sql, transaction);
        return !exists;
    }
}
