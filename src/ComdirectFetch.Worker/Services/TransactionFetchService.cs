using ComdirectFetch.Api;
using ComdirectFetch.Data;
using ComdirectFetch.Domain;
using Microsoft.Extensions.Options;

namespace ComdirectFetch.Worker.Services;

/// <summary>
/// Ruft Kontoumsätze je bekanntem Konto ab, speichert jede Buchung nur einmal (KONZEPT.md
/// Abschnitt 5) und stößt danach die Kategorisierung neuer Umsätze an (Abschnitt 6).
/// </summary>
public sealed class TransactionFetchService(
    ComdirectAuthCoordinator authCoordinator,
    ComdirectBankingClient bankingClient,
    AccountRepository accountRepository,
    TransactionRepository transactionRepository,
    CategorizationService categorizationService,
    SyncLogRepository syncLogRepository,
    IOptions<FetchOptions> options,
    ILogger<TransactionFetchService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.TransactionsIntervalSeconds);
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
        var accounts = await accountRepository.GetAllAsync(stoppingToken);
        var newTransactions = 0;

        foreach (var account in accounts)
        {
            var logId = await syncLogRepository.InsertAsync(new SyncLogEntry
            {
                DataKind = SyncDataKind.Kontoumsaetze,
                AccountId = account.Id,
                ApplicationVersion = AppVersion.Current,
                StartedAt = DateTimeOffset.UtcNow,
                Status = SyncStatus.Erfolgreich,
            }, stoppingToken);

            try
            {
                var entries = await bankingClient.GetTransactionsAsync(token, account.ComdirectAccountId, stoppingToken);
                var now = DateTimeOffset.UtcNow;

                foreach (var entry in entries)
                {
                    var valueDate = DateOnly.TryParse(entry.ValutaDate, out var valuta) ? valuta : (DateOnly?)null;
                    var bookingDate = entry.BookingDate ?? valueDate;
                    if (bookingDate is null)
                    {
                        // Ohne Buchungs- oder Valuta-Datum (z. B. noch nicht gebuchte Umsätze) lässt
                        // sich der Umsatz sinnvoll erst beim nächsten Abruf mit gesetztem Datum speichern.
                        logger.LogWarning("Umsatz {Reference} ohne Buchungsdatum übersprungen.", entry.Reference);
                        continue;
                    }

                    // Gegenkonto-IBAN: bei Gutschrift (Betrag >= 0) ist der Remitter die Gegenseite,
                    // bei Belastung Debtor/Creditor (comdirect befüllt je nach Buchungsart nur eines davon).
                    var counterpartyIban = entry.Amount.Value >= 0
                        ? entry.Remitter?.Iban
                        : entry.Debtor?.Iban ?? entry.Creditor?.Iban;

                    var inserted = await transactionRepository.InsertIfNewAsync(new Transaction
                    {
                        AccountId = account.Id,
                        ComdirectReference = entry.Reference,
                        BookingDate = bookingDate.Value,
                        ValueDate = valueDate,
                        Amount = entry.Amount.Value,
                        Currency = entry.Amount.Unit,
                        BookingText = entry.RemittanceInfo,
                        TransactionType = entry.TransactionType?.Text ?? entry.TransactionType?.Key,
                        CounterpartyIban = counterpartyIban,
                        FirstSeenAt = now,
                    }, stoppingToken);

                    if (inserted)
                    {
                        newTransactions++;
                    }
                }

                await syncLogRepository.CompleteAsync(logId, SyncStatus.Erfolgreich, DateTimeOffset.UtcNow, cancellationToken: stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Umsatzabruf für Konto {AccountId} fehlgeschlagen.", account.ComdirectAccountId);
                await syncLogRepository.CompleteAsync(logId, SyncStatus.Fehlgeschlagen, DateTimeOffset.UtcNow, ex.Message, stoppingToken);
            }

            // Kleine Pause zwischen Konten – schont comdirects Rate-Limit, besonders wenn ein
            // vorheriges Konto schon mehrere Pagination-Seiten abgerufen hat (live beobachtet).
            await Task.Delay(TimeSpan.FromMilliseconds(300), stoppingToken);
        }

        if (newTransactions > 0)
        {
            await categorizationService.CategorizeNewTransactionsAsync(stoppingToken);
        }
    }
}
