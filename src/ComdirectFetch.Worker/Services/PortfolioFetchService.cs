using ComdirectFetch.Api;
using ComdirectFetch.Data;
using ComdirectFetch.Domain;
using Microsoft.Extensions.Options;

namespace ComdirectFetch.Worker.Services;

/// <summary>Ruft die Depotübersicht je Depot ab und schreibt Snapshot + Positionen (KONZEPT.md Abschnitt 3/5).</summary>
public sealed class PortfolioFetchService(
    ComdirectAuthCoordinator authCoordinator,
    ComdirectBrokerageClient brokerageClient,
    PortfolioRepository portfolioRepository,
    PortfolioSnapshotRepository snapshotRepository,
    SyncLogRepository syncLogRepository,
    IOptions<FetchOptions> options,
    ILogger<PortfolioFetchService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.PortfolioIntervalSeconds);
        using var timer = new PeriodicTimer(interval);

        await RunOnceAsync(stoppingToken);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnceAsync(stoppingToken);
        }
    }

    /// <summary>Ein einzelner Abruflauf; auch manuell über POST /debug/fetch-now auslösbar.</summary>
    public async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        if (authCoordinator.State != AuthState.Authentifiziert || authCoordinator.CurrentToken is null)
        {
            return;
        }

        var token = authCoordinator.CurrentToken;
        IReadOnlyList<DepotEntry> depots;
        try
        {
            depots = await brokerageClient.GetDepotsAsync(token, stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Depotliste konnte nicht abgerufen werden.");
            return;
        }

        foreach (var depot in depots)
        {
            var portfolioId = await portfolioRepository.UpsertAsync(new Portfolio
            {
                ComdirectPortfolioId = depot.DepotId,
                DisplayName = depot.DepotDisplayId ?? depot.DepotId,
            }, stoppingToken);

            var logId = await syncLogRepository.InsertAsync(new SyncLogEntry
            {
                DataKind = SyncDataKind.Depotuebersicht,
                PortfolioId = portfolioId,
                ApplicationVersion = AppVersion.Current,
                StartedAt = DateTimeOffset.UtcNow,
                Status = SyncStatus.Erfolgreich,
            }, stoppingToken);

            try
            {
                var positions = await brokerageClient.GetDepotPositionsAsync(token, depot.DepotId, stoppingToken);
                var now = DateTimeOffset.UtcNow;
                var currency = positions.Aggregated?.CurrentValue?.Unit
                    ?? positions.Values.FirstOrDefault()?.CurrentValue.Unit
                    ?? "EUR";

                var snapshotId = await snapshotRepository.InsertSnapshotAsync(new PortfolioSnapshot
                {
                    PortfolioId = portfolioId,
                    Timestamp = now,
                    TotalValue = positions.Aggregated?.CurrentValue?.Value ?? positions.Values.Sum(p => p.CurrentValue.Value),
                    AcquisitionValue = positions.Aggregated?.PurchaseValue?.Value ?? positions.Values.Sum(p => p.PurchaseValue.Value),
                    Currency = currency,
                }, stoppingToken);

                var domainPositions = positions.Values.Select(p => new PortfolioPosition
                {
                    SnapshotId = snapshotId,
                    Isin = p.Instrument?.Isin,
                    Wkn = p.Wkn ?? p.Instrument?.Wkn,
                    DisplayName = p.Instrument?.Name ?? p.Wkn ?? "unbekannt",
                    Quantity = p.Quantity.Value,
                    MarketValue = p.CurrentValue.Value,
                    AcquisitionValue = p.PurchaseValue.Value,
                    ProfitLoss = p.ProfitLossAbsolute?.Value ?? (p.CurrentValue.Value - p.PurchaseValue.Value),
                    Currency = p.CurrentValue.Unit,
                });

                await snapshotRepository.InsertPositionsAsync(domainPositions, stoppingToken);
                await syncLogRepository.CompleteAsync(logId, SyncStatus.Erfolgreich, DateTimeOffset.UtcNow, cancellationToken: stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Depotübersicht für Depot {DepotId} fehlgeschlagen.", depot.DepotId);
                await syncLogRepository.CompleteAsync(logId, SyncStatus.Fehlgeschlagen, DateTimeOffset.UtcNow, ex.Message, stoppingToken);
            }
        }
    }
}
