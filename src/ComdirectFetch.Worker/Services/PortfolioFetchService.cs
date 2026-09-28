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
    PortfolioSettlementAccountRepository settlementAccountRepository,
    AccountRepository accountRepository,
    TransactionRepository transactionRepository,
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

            await UpdateSettlementAccountLinksAsync(portfolioId, depot, stoppingToken);

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
                    InstrumentType = p.Instrument?.StaticData?.InstrumentType,
                    DisplayName = p.Instrument?.Name ?? p.Wkn ?? "unbekannt",
                    Quantity = p.Quantity.Value,
                    MarketValue = p.CurrentValue.Value,
                    AcquisitionValue = p.PurchaseValue.Value,
                    ProfitLoss = p.ProfitLossAbsolute?.Value ?? (p.CurrentValue.Value - p.PurchaseValue.Value),
                    Currency = p.CurrentValue.Unit,
                });

                await snapshotRepository.InsertPositionsAsync(domainPositions, stoppingToken);
                await RecomputePerformanceAsync(portfolioId, snapshotId, stoppingToken);
                await syncLogRepository.CompleteAsync(logId, SyncStatus.Erfolgreich, DateTimeOffset.UtcNow, cancellationToken: stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Depotübersicht für Depot {DepotId} fehlgeschlagen.", depot.DepotId);
                await syncLogRepository.CompleteAsync(logId, SyncStatus.Fehlgeschlagen, DateTimeOffset.UtcNow, ex.Message, stoppingToken);
            }
        }
    }

    /// <summary>
    /// Löst defaultSettlementAccountId/settlementAccountIds (Issue #12) gegen die gespeicherten
    /// Konten auf (Abgleich über accounts.comdirect_account_id) und ersetzt die Verknüpfungsliste.
    /// Liefert comdirect kein bekanntes Konto (z. B. Format weicht ab oder Konto noch nicht
    /// erfasst), bleibt das Depot ohne Verknüpfung - RecomputePerformanceAsync liefert dann
    /// einfach keine Kennzahlen (null), kein Fehler.
    /// </summary>
    private async Task UpdateSettlementAccountLinksAsync(long portfolioId, DepotEntry depot, CancellationToken ct)
    {
        var accountsByComdirectId = (await accountRepository.GetAllAsync(ct))
            .ToDictionary(a => a.ComdirectAccountId, a => a.Id);

        var links = new List<(long AccountId, bool IsDefault)>();
        if (depot.DefaultSettlementAccountId is not null &&
            accountsByComdirectId.TryGetValue(depot.DefaultSettlementAccountId, out var defaultAccountId))
        {
            links.Add((defaultAccountId, true));
        }

        foreach (var otherId in depot.SettlementAccountIds ?? [])
        {
            if (accountsByComdirectId.TryGetValue(otherId, out var accountId) && links.All(l => l.AccountId != accountId))
            {
                links.Add((accountId, false));
            }
        }

        await settlementAccountRepository.ReplaceForPortfolioAsync(portfolioId, links, ct);
    }

    /// <summary>
    /// Berechnet Netto-Kapitaleinsatz, erhaltene Dividenden und die tagesverkettete Time-Weighted
    /// Return (Issue #12, ComdirectFetch.Domain.DepotPerformanceCalculator) neu aus der kompletten
    /// Historie und trägt sie am gerade eingefügten Snapshot nach. Bewusst jedes Mal komplett neu
    /// statt inkrementell fortgeschrieben - bei der aktuellen Datenmenge (Snapshots/Umsätze im
    /// niedrigen drei- bis vierstelligen Bereich) vernachlässigbar teuer und deutlich weniger
    /// fehleranfällig als eine verkettete Fortschreibung.
    /// </summary>
    private async Task RecomputePerformanceAsync(long portfolioId, long snapshotId, CancellationToken ct)
    {
        var settlementAccountIds = await settlementAccountRepository.GetAccountIdsForPortfolioAsync(portfolioId, ct);
        if (settlementAccountIds.Count == 0)
        {
            return;
        }

        var settlementTransactions = await transactionRepository.GetForAccountsAsync(settlementAccountIds, ct);
        var valuationHistory = await snapshotRepository.GetValuationHistoryAsync(portfolioId, ct);
        var latestValue = valuationHistory[^1].TotalValue;

        var netInvested = DepotPerformanceCalculator.CalculateNetInvestedCapital(latestValue, settlementTransactions);

        var dailyValuations = DepotPerformanceCalculator.ConsolidateToDailyLastValue(
            valuationHistory.Select(v => (v.Timestamp, v.TotalValue)).ToList());
        var twr = DepotPerformanceCalculator.CalculateTimeWeightedReturn(dailyValuations, settlementTransactions);

        await snapshotRepository.UpdatePerformanceAsync(
            snapshotId, netInvested.NetInvestedCapital, netInvested.DividendsReceived, twr?.ReturnPercent, ct);
    }
}
