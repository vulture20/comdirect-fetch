using Dapper;

namespace ComdirectFetch.Data;

/// <summary>
/// Konsolidierungs- und Aufräum-Queries für account_balances/portfolio_snapshots/
/// portfolio_positions/sync_log (KONZEPT.md Abschnitt 11). Bewusst tabellenübergreifend statt
/// "eine Repository-Klasse pro Tabelle" (wie sonst in diesem Projekt üblich) - genau wie
/// DiagnosticsRepository ist das hier ein cross-cutting operativer Belang, kein fachlicher
/// Datenzugriff für eine einzelne Entität. transactions wird bewusst nirgends angefasst (Konzept:
/// Finanz-Ledger, nie konsolidieren/löschen).
///
/// Konsolidierung und Löschung laufen NICHT in einer expliziten DB-Transaktion - wie der Rest
/// dieses Projekts (auch Snapshot+Positionen-Insert läuft ohne Transaktion) wird ein möglicher
/// Absturz zwischen zwei Schritten in Kauf genommen; alle Operationen hier sind idempotent
/// (ein erneuter Lauf holt einen unterbrochenen Zustand einfach nach), sodass kein dauerhaft
/// inkonsistenter Zustand entstehen kann.
/// </summary>
public sealed class RetentionRepository(IDbConnectionFactory connectionFactory)
{
    /// <summary>
    /// Reduziert account_balances-Zeilen mit recorded_at vor rawDataCutoff auf eine Zeile pro
    /// (account_id, Tag) - behalten wird die Zeile mit der höchsten id des Tages (entspricht dem
    /// zeitlich letzten Wert, da id monoton mit recorded_at steigt). Gibt die Anzahl gelöschter
    /// Zeilen zurück.
    /// </summary>
    public async Task<int> ConsolidateAccountBalancesAsync(DateTimeOffset rawDataCutoff, CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        const string sql = """
            DELETE ab FROM account_balances ab
            INNER JOIN (
                SELECT account_id, DATE(recorded_at) AS day, MAX(id) AS keep_id
                FROM account_balances
                WHERE recorded_at < @Cutoff
                GROUP BY account_id, DATE(recorded_at)
            ) keep ON keep.account_id = ab.account_id AND DATE(ab.recorded_at) = keep.day
            WHERE ab.recorded_at < @Cutoff AND ab.id <> keep.keep_id;
            """;

        return await connection.ExecuteAsync(sql, new { Cutoff = rawDataCutoff });
    }

    /// <summary>Analog zu <see cref="ConsolidateAccountBalancesAsync"/>, für portfolio_snapshots je (portfolio_id, Tag) - löscht zuerst die Positionen der wegfallenden Snapshots (Fremdschlüssel), dann die Snapshots selbst.</summary>
    public async Task<(int SnapshotsDeleted, int PositionsDeleted)> ConsolidatePortfolioSnapshotsAsync(
        DateTimeOffset rawDataCutoff, CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        const string deletePositionsSql = """
            DELETE pp FROM portfolio_positions pp
            INNER JOIN portfolio_snapshots ps ON ps.id = pp.snapshot_id
            INNER JOIN (
                SELECT portfolio_id, DATE(recorded_at) AS day, MAX(id) AS keep_id
                FROM portfolio_snapshots
                WHERE recorded_at < @Cutoff
                GROUP BY portfolio_id, DATE(recorded_at)
            ) keep ON keep.portfolio_id = ps.portfolio_id AND DATE(ps.recorded_at) = keep.day
            WHERE ps.recorded_at < @Cutoff AND ps.id <> keep.keep_id;
            """;
        var positionsDeleted = await connection.ExecuteAsync(deletePositionsSql, new { Cutoff = rawDataCutoff });

        const string deleteSnapshotsSql = """
            DELETE ps FROM portfolio_snapshots ps
            INNER JOIN (
                SELECT portfolio_id, DATE(recorded_at) AS day, MAX(id) AS keep_id
                FROM portfolio_snapshots
                WHERE recorded_at < @Cutoff
                GROUP BY portfolio_id, DATE(recorded_at)
            ) keep ON keep.portfolio_id = ps.portfolio_id AND DATE(ps.recorded_at) = keep.day
            WHERE ps.recorded_at < @Cutoff AND ps.id <> keep.keep_id;
            """;
        var snapshotsDeleted = await connection.ExecuteAsync(deleteSnapshotsSql, new { Cutoff = rawDataCutoff });

        return (snapshotsDeleted, positionsDeleted);
    }

    /// <summary>Löscht account_balances-Zeilen vollständig, deren recorded_at vor deleteCutoff liegt (bereits konsolidierte Alt-Daten).</summary>
    public async Task<int> DeleteOldAccountBalancesAsync(DateTimeOffset deleteCutoff, CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(
            "DELETE FROM account_balances WHERE recorded_at < @Cutoff;", new { Cutoff = deleteCutoff });
    }

    /// <summary>Analog zu <see cref="DeleteOldAccountBalancesAsync"/> für portfolio_snapshots inkl. ihrer portfolio_positions.</summary>
    public async Task<(int SnapshotsDeleted, int PositionsDeleted)> DeleteOldPortfolioSnapshotsAsync(
        DateTimeOffset deleteCutoff, CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        var positionsDeleted = await connection.ExecuteAsync(
            """
            DELETE pp FROM portfolio_positions pp
            INNER JOIN portfolio_snapshots ps ON ps.id = pp.snapshot_id
            WHERE ps.recorded_at < @Cutoff;
            """, new { Cutoff = deleteCutoff });

        var snapshotsDeleted = await connection.ExecuteAsync(
            "DELETE FROM portfolio_snapshots WHERE recorded_at < @Cutoff;", new { Cutoff = deleteCutoff });

        return (snapshotsDeleted, positionsDeleted);
    }

    /// <summary>Löscht alte sync_log-Zeilen - eigenständige, einfachere Politik ohne Konsolidierungsstufe.</summary>
    public async Task<int> DeleteOldSyncLogAsync(DateTimeOffset deleteCutoff, CancellationToken cancellationToken = default)
    {
        using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(
            "DELETE FROM sync_log WHERE started_at < @Cutoff;", new { Cutoff = deleteCutoff });
    }
}
