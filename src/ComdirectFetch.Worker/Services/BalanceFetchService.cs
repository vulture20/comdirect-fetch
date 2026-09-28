using ComdirectFetch.Api;
using ComdirectFetch.Data;
using ComdirectFetch.Domain;
using Microsoft.Extensions.Options;

namespace ComdirectFetch.Worker.Services;

/// <summary>Ruft Salden für alle Konten/Depots ab und schreibt sie als Zeitreihe (KONZEPT.md Abschnitt 3/5).</summary>
public sealed class BalanceFetchService(
    ComdirectAuthCoordinator authCoordinator,
    ComdirectBankingClient bankingClient,
    AccountRepository accountRepository,
    AccountBalanceRepository accountBalanceRepository,
    SyncLogRepository syncLogRepository,
    IOptions<FetchOptions> options,
    ILogger<BalanceFetchService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.BalancesIntervalSeconds);
        using var timer = new PeriodicTimer(interval);

        // Nicht erst ein volles Intervall warten, bevor überhaupt zum ersten Mal abgerufen wird.
        await RunOnceAsync(stoppingToken);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnceAsync(stoppingToken);
        }
    }

    /// <summary>Ein einzelner Abruflauf; auch manuell über POST /debug/fetch-now auslösbar.</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        if (authCoordinator.State != AuthState.Authentifiziert || authCoordinator.CurrentToken is null)
        {
            return;
        }

        var logId = await syncLogRepository.InsertAsync(new SyncLogEntry
        {
            DataKind = SyncDataKind.Salden,
            ApplicationVersion = AppVersion.Current,
            StartedAt = DateTimeOffset.UtcNow,
            Status = SyncStatus.Erfolgreich,
        }, cancellationToken);

        try
        {
            var entries = await bankingClient.GetBalancesAsync(authCoordinator.CurrentToken, cancellationToken);
            logger.LogInformation(
                "Saldenabruf lieferte {Count} Konto(s): {Ibans}",
                entries.Count,
                string.Join(", ", entries.Select(e => e.Account.Iban is { } iban ? IbanMasking.Mask(iban) : e.Account.AccountId)));
            var now = DateTimeOffset.UtcNow;

            foreach (var entry in entries)
            {
                var accountId = await accountRepository.UpsertAsync(new Account
                {
                    ComdirectAccountId = entry.Account.AccountId,
                    Iban = entry.Account.Iban,
                    AccountType = entry.Account.AccountType?.Text ?? entry.Account.AccountType?.Key ?? "unbekannt",
                    DisplayName = entry.Account.Iban ?? entry.Account.AccountId,
                    Currency = entry.Account.Currency,
                }, cancellationToken);

                await accountBalanceRepository.InsertAsync(new AccountBalance
                {
                    AccountId = accountId,
                    Timestamp = now,
                    Balance = entry.Balance.Value,
                    AvailableAmount = entry.AvailableCashAmount.Value,
                    Currency = entry.Balance.Unit,
                }, cancellationToken);
            }

            await syncLogRepository.CompleteAsync(logId, SyncStatus.Erfolgreich, DateTimeOffset.UtcNow, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Saldenabruf fehlgeschlagen.");
            await syncLogRepository.CompleteAsync(logId, SyncStatus.Fehlgeschlagen, DateTimeOffset.UtcNow, ex.Message, cancellationToken);
        }
    }
}
