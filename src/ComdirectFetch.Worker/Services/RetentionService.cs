using ComdirectFetch.Data;
using ComdirectFetch.Domain;
using Microsoft.Extensions.Options;

namespace ComdirectFetch.Worker.Services;

/// <summary>
/// Konsolidiert und räumt alte Zeitreihen-Daten auf (KONZEPT.md Abschnitt 11): account_balances/
/// portfolio_snapshots werden nach Comdirect-unabhängiger Rohdaten-Frist auf eine Zeile/Tag
/// reduziert, optional nach einer weiteren Frist komplett gelöscht; sync_log separat nach eigener
/// Frist gelöscht. Komplett opt-in - ohne gesetzte Retention__*-Zeiträume tut RunOnceAsync nichts
/// (kein sync_log-Rauschen). transactions wird nie angefasst (Finanz-Ledger). Berührt weder
/// Session noch TAN.
/// </summary>
public sealed class RetentionService(
    RetentionRepository retentionRepository,
    SyncLogRepository syncLogRepository,
    IOptions<RetentionOptions> options,
    ILogger<RetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.IntervalSeconds);
        using var timer = new PeriodicTimer(interval);

        // Nicht erst ein volles Intervall warten, bevor überhaupt zum ersten Mal aufgeräumt wird.
        await RunOnceAsync(stoppingToken);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnceAsync(stoppingToken);
        }
    }

    /// <summary>Ein einzelner Konsolidierungs-/Aufräumlauf; auch manuell über POST /debug/consolidate auslösbar.</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var opts = options.Value;
        if (opts.RawDataRetentionDays is null && opts.SyncLogRetentionDays is null)
        {
            return;
        }

        var logId = await syncLogRepository.InsertAsync(new SyncLogEntry
        {
            DataKind = SyncDataKind.Konsolidierung,
            ApplicationVersion = AppVersion.Current,
            StartedAt = DateTimeOffset.UtcNow,
            Status = SyncStatus.Erfolgreich,
        }, cancellationToken);

        try
        {
            var summary = new List<string>();
            var now = DateTimeOffset.UtcNow;

            if (opts.RawDataRetentionDays is { } rawDays)
            {
                var rawCutoff = now - TimeSpan.FromDays(rawDays);
                var balancesConsolidated = await retentionRepository.ConsolidateAccountBalancesAsync(rawCutoff, cancellationToken);
                var (snapshotsConsolidated, positionsConsolidated) =
                    await retentionRepository.ConsolidatePortfolioSnapshotsAsync(rawCutoff, cancellationToken);
                summary.Add($"{balancesConsolidated} Salden-Zeilen konsolidiert");
                summary.Add($"{snapshotsConsolidated} Depot-Snapshots ({positionsConsolidated} Positionen) konsolidiert");

                // Gesamtalter ab "jetzt" = Rohdaten-Frist + zusätzliche Konsolidiert-Frist - siehe
                // RetentionOptions.ConsolidatedDataRetentionDays.
                if (opts.ConsolidatedDataRetentionDays is { } consolidatedDays)
                {
                    var deleteCutoff = now - TimeSpan.FromDays(rawDays + consolidatedDays);
                    var balancesDeleted = await retentionRepository.DeleteOldAccountBalancesAsync(deleteCutoff, cancellationToken);
                    var (snapshotsDeleted, positionsDeleted) =
                        await retentionRepository.DeleteOldPortfolioSnapshotsAsync(deleteCutoff, cancellationToken);
                    summary.Add($"{balancesDeleted} Salden-Zeilen gelöscht");
                    summary.Add($"{snapshotsDeleted} Depot-Snapshots ({positionsDeleted} Positionen) gelöscht");
                }
            }

            if (opts.SyncLogRetentionDays is { } syncLogDays)
            {
                var syncLogCutoff = now - TimeSpan.FromDays(syncLogDays);
                var syncLogDeleted = await retentionRepository.DeleteOldSyncLogAsync(syncLogCutoff, cancellationToken);
                summary.Add($"{syncLogDeleted} sync_log-Zeilen gelöscht");
            }

            var message = summary.Count > 0 ? string.Join("; ", summary) : "nichts zu tun";
            logger.LogInformation("Konsolidierung/Aufräumen abgeschlossen: {Summary}", message);
            await syncLogRepository.CompleteAsync(logId, SyncStatus.Erfolgreich, DateTimeOffset.UtcNow, message, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Konsolidierung/Aufräumen fehlgeschlagen.");
            await syncLogRepository.CompleteAsync(logId, SyncStatus.Fehlgeschlagen, DateTimeOffset.UtcNow, ex.Message, cancellationToken);
        }
    }
}
