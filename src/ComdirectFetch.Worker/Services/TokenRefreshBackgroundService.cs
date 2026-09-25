using ComdirectFetch.Data;
using ComdirectFetch.Domain;
using Microsoft.Extensions.Options;

namespace ComdirectFetch.Worker.Services;

/// <summary>
/// Hält die comdirect-Session am Leben, solange der Dienst durchgehend läuft (KONZEPT.md
/// Abschnitt 3): läuft deutlich häufiger als die Datenabruf-Intervalle, damit weder Access-
/// noch Refresh-Token ablaufen. Bricht die Kette ab (Neustart, Downtime), wechselt der
/// ComdirectAuthCoordinator in den Zustand "NichtAuthentifiziert" und eine manuelle
/// TAN-Freigabe über /auth/start und /auth/confirm ist wieder nötig.
/// </summary>
public sealed class TokenRefreshBackgroundService(
    ComdirectAuthCoordinator authCoordinator,
    SyncLogRepository syncLogRepository,
    IOptions<FetchOptions> options,
    ILogger<TokenRefreshBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.TokenRefreshIntervalSeconds);
        using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (authCoordinator.State != AuthState.Authentifiziert)
            {
                continue;
            }

            var logId = await syncLogRepository.InsertAsync(new SyncLogEntry
            {
                DataKind = SyncDataKind.TokenRefresh,
                ApplicationVersion = AppVersion.Current,
                StartedAt = DateTimeOffset.UtcNow,
                Status = SyncStatus.Erfolgreich,
            }, stoppingToken);

            try
            {
                await authCoordinator.RefreshAsync(stoppingToken);
                await syncLogRepository.CompleteAsync(logId, SyncStatus.Erfolgreich, DateTimeOffset.UtcNow, cancellationToken: stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Token-Refresh fehlgeschlagen.");
                await syncLogRepository.CompleteAsync(
                    logId, SyncStatus.FreigabeErforderlich, DateTimeOffset.UtcNow, ex.Message, stoppingToken);
            }
        }
    }
}
