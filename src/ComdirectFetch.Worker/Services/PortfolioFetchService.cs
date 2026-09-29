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
    AccountBalanceRepository accountBalanceRepository,
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
            logger.LogInformation(
                "Depotabruf lieferte {Count} Depot(s): {DisplayIds}",
                depots.Count, string.Join(", ", depots.Select(d => d.DepotDisplayId ?? d.DepotId)));
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
                await RecomputePerformanceAsync(portfolioId, stoppingToken);
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
    /// Berechnet Guthaben, Kapitalbasis, Dividenden und die tagesverkettete Time-Weighted
    /// Return (Issue #12, ComdirectFetch.Domain.DepotPerformanceCalculator) für ALLE Snapshots ab dem
    /// Bezugspunkt neu aus der kompletten Historie und trägt nur die Zeilen nach, deren gespeicherte
    /// Werte abweichen. Bewusst nicht inkrementell fortgeschrieben: bei der aktuellen Datenmenge
    /// (Snapshots im niedrigen vierstelligen Bereich) vernachlässigbar teuer, deutlich weniger
    /// fehleranfällig - und selbstheilend: ein nachträglich auftauchender Umsatz korrigiert auch
    /// die davorliegenden Zeilen, und der erste Lauf nach einer Modelländerung füllt die Historie auf.
    /// </summary>
    private async Task RecomputePerformanceAsync(long portfolioId, CancellationToken ct)
    {
        var links = await settlementAccountRepository.GetLinksAsync(portfolioId, ct);
        var defaultAccountIds = links.Where(l => l.IsDefault).Select(l => l.AccountId).ToList();
        if (defaultAccountIds.Count == 0)
        {
            return;
        }

        var transactions = (await transactionRepository.GetForAccountsAsync(links.Select(l => l.AccountId), ct))
            .Select(t => new LinkedTransaction(t, defaultAccountIds.Contains(t.AccountId)))
            .ToList();
        var latestBalances = await accountBalanceRepository.GetLatestBalancesAsync(defaultAccountIds, ct);
        if (latestBalances.Count < defaultAccountIds.Count)
        {
            return;
        }

        var rows = await snapshotRepository.GetPerformanceRowsAsync(portfolioId, ct);

        var points = DepotPerformanceCalculator.Calculate(
            rows.Select(r => new DepotSnapshotInput(r.SnapshotId, r.Timestamp, r.PositionsValue)).ToList(),
            latestBalances, transactions);

        var rowsById = rows.ToDictionary(r => r.SnapshotId);
        foreach (var point in points)
        {
            var cash = Math.Round(point.SettlementCash, 2);
            var capital = Math.Round(point.NetInvestedCapital, 2);
            var dividends = Math.Round(point.DividendsReceived, 2);
            var twr = point.TimeWeightedReturnPercent is { } value ? Math.Round(value, 4) : (decimal?)null;

            var stored = rowsById[point.SnapshotId];
            if (stored.SettlementCash == cash
                && stored.NetInvestedCapital == capital && stored.DividendsReceived == dividends
                && stored.TimeWeightedReturnPercent == twr)
            {
                continue;
            }

            await snapshotRepository.UpdatePerformanceAsync(point.SnapshotId, cash, capital, dividends, twr, ct);
        }
    }
}
