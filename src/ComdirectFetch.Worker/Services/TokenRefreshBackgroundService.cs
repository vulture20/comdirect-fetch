using ComdirectFetch.Data;
using ComdirectFetch.Domain;
using Microsoft.Extensions.Options;

namespace ComdirectFetch.Worker.Services;

/// <summary>
/// Hält die comdirect-Session am Leben, solange der Dienst durchgehend läuft (KONZEPT.md
/// Abschnitt 3): läuft deutlich häufiger als die Datenabruf-Intervalle, damit weder Access-
/// noch Refresh-Token ablaufen. Vorübergehende Fehler (Netzwerk, Timeout) lassen den Token
/// unangetastet und werden kurz darauf erneut versucht (seit 1.2.2). Lehnt comdirect den Token
/// ab, wechselt der ComdirectAuthCoordinator in den Zustand "NichtAuthentifiziert" und eine
/// manuelle TAN-Freigabe über /auth/start und /auth/confirm ist wieder nötig.
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

            await RefreshWithShortRetriesAsync(logId, stoppingToken);
        }
    }

    /// <summary>
    /// Bei einem vorübergehenden Fehler (Netzwerk, Timeout) wird innerhalb desselben Ticks mehrmals kurz
    /// nachgefasst, damit der Token nicht bis zum nächsten regulären Tick ungenutzt altert. Das ist
    /// gefahrlos, weil ein Refresh keine TAN-Challenge auslöst. Die Pausen summieren sich bewusst auf
    /// deutlich unter das Refresh-Intervall.
    /// </summary>
    private async Task RefreshWithShortRetriesAsync(long logId, CancellationToken stoppingToken)
    {
        TimeSpan[] retryDelays = [TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120)];
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await authCoordinator.RefreshAsync(stoppingToken);
                await syncLogRepository.CompleteAsync(logId, SyncStatus.Erfolgreich, DateTimeOffset.UtcNow, cancellationToken: stoppingToken);
                return;
            }
            catch (TransientRefreshException ex) when (attempt < retryDelays.Length)
            {
                logger.LogWarning("Token-Refresh vorübergehend fehlgeschlagen, neuer Versuch in {Delay}: {Message}", retryDelays[attempt], ex.Message);
                await Task.Delay(retryDelays[attempt], stoppingToken);
            }
            catch (TransientRefreshException ex)
            {
                // Token bleibt erhalten, der nächste reguläre Tick versucht es erneut.
                await syncLogRepository.CompleteAsync(logId, SyncStatus.Fehlgeschlagen, DateTimeOffset.UtcNow, ex.Message, stoppingToken);
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Token-Refresh fehlgeschlagen.");
                await syncLogRepository.CompleteAsync(
                    logId, SyncStatus.FreigabeErforderlich, DateTimeOffset.UtcNow, ex.Message, stoppingToken);
                return;
            }
        }
    }
}
