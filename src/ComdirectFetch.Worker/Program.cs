using ComdirectFetch.Api;
using ComdirectFetch.Data;
using ComdirectFetch.Domain;
using ComdirectFetch.Worker;
using ComdirectFetch.Worker.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ComdirectApiOptions>(builder.Configuration.GetSection(ComdirectApiOptions.SectionName));
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.SectionName));
builder.Services.Configure<FetchOptions>(builder.Configuration.GetSection(FetchOptions.SectionName));

builder.Services.AddSingleton<ComdirectRequestContext>();
builder.Services.AddHttpClient<ComdirectAuthClient>();
builder.Services.AddHttpClient<ComdirectBankingClient>();
builder.Services.AddHttpClient<ComdirectBrokerageClient>();
builder.Services.AddSingleton<ComdirectAuthCoordinator>();

builder.Services.AddSingleton<IDbConnectionFactory>(sp =>
{
    var dbOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DatabaseOptions>>().Value;
    return new MySqlDbConnectionFactory(dbOptions.BuildConnectionString());
});
builder.Services.AddSingleton<DatabaseMigrator>();
builder.Services.AddSingleton<AccountRepository>();
builder.Services.AddSingleton<AccountBalanceRepository>();
builder.Services.AddSingleton<PortfolioRepository>();
builder.Services.AddSingleton<PortfolioSnapshotRepository>();
builder.Services.AddSingleton<TransactionRepository>();
builder.Services.AddSingleton<CategoryRepository>();
builder.Services.AddSingleton<CategorizationRuleRepository>();
builder.Services.AddSingleton<SyncLogRepository>();
builder.Services.AddSingleton<DiagnosticsRepository>();
builder.Services.AddSingleton<AuthTokenRepository>();
builder.Services.AddSingleton<CategorizationService>();

builder.Services.AddHostedService<TokenRefreshBackgroundService>();

// Als konkrete Singletons registriert (statt nur AddHostedService), damit RunOnceAsync auch
// manuell über POST /debug/fetch-now angestoßen werden kann, ohne auf das Intervall zu warten.
builder.Services.AddSingleton<BalanceFetchService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BalanceFetchService>());
builder.Services.AddSingleton<PortfolioFetchService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PortfolioFetchService>());
builder.Services.AddSingleton<TransactionFetchService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TransactionFetchService>());

var app = builder.Build();

// Datenbank-Schema beim Start auf den neuesten Stand bringen (KONZEPT.md Abschnitt 8:
// append-only Migrationen aus db/migrations/, DbUp führt die Historie in der DB selbst).
using (var scope = app.Services.CreateScope())
{
    var dbOptions = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DatabaseOptions>>().Value;
    scope.ServiceProvider.GetRequiredService<DatabaseMigrator>().MigrateToLatest(dbOptions.BuildConnectionString());
}

// KONZEPT.md Abschnitt 3/9: versucht, eine vorher persistierte Session wiederherzustellen,
// bevor der Dienst startet - ohne Comdirect__TokenEncryptionKeyBase64 oder ohne gespeicherten
// gültigen Token bleibt es beim bisherigen Verhalten (/auth/start nötig).
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<ComdirectAuthCoordinator>().TryRestoreAsync();
}

// KONZEPT.md Abschnitt 3: TAN-Freigabe kann der Container nicht automatisch erledigen.
// /auth/start löst die TAN-Challenge aus (z. B. PushTAN-Benachrichtigung), /auth/confirm
// schließt die Freigabe ab (ggf. mit manuell eingegebenem TAN-Code bei photoTAN/mobileTAN).
app.MapPost("/auth/start", async (ComdirectAuthCoordinator coordinator, CancellationToken ct) =>
{
    var challenge = await coordinator.StartAsync(ct);
    return Results.Ok(new { challenge.Typ, Message = "Bitte TAN in der comdirect-App bestätigen, danach POST /auth/confirm aufrufen." });
});

app.MapPost("/auth/confirm", async (ComdirectAuthCoordinator coordinator, TanConfirmRequest? body, CancellationToken ct) =>
{
    await coordinator.ConfirmAsync(body?.TanCode, ct);
    return Results.Ok(new { Message = "Session freigegeben." });
});

app.MapGet("/health", (ComdirectAuthCoordinator coordinator) => Results.Ok(new
{
    Version = AppVersion.Current,
    AuthState = coordinator.State.ToString(),
}));

// Betriebs-/Test-Hilfsmittel: stößt alle drei Datenabrufe sofort an, statt auf die
// konfigurierten Intervalle zu warten. Berührt weder Session noch TAN.
app.MapPost("/debug/fetch-now", async (
    BalanceFetchService balances,
    PortfolioFetchService portfolio,
    TransactionFetchService transactions,
    CancellationToken ct) =>
{
    await balances.RunOnceAsync(ct);
    await portfolio.RunOnceAsync(ct);
    await transactions.RunOnceAsync(ct);
    return Results.Ok(new { Message = "Abruf angestoßen, siehe sync_log für Details." });
});

app.MapGet("/debug/summary", async (DiagnosticsRepository diagnostics, CancellationToken ct) => Results.Ok(new
{
    Counts = await diagnostics.GetTableCountsAsync(ct),
    RecentSyncLog = (await diagnostics.GetRecentSyncLogAsync(ct))
        .Select(e => new { e.DataKind, e.Status, e.ErrorMessage }),
}));

// Betriebs-Hilfsmittel: wendet die Kategorisierungsregeln erneut auf alle nicht manuell
// kategorisierten Umsätze an - z. B. nach einer Erweiterung/Korrektur der Regeln
// (KONZEPT.md Abschnitt 6/9). Berührt weder Session noch TAN, gefahrlos wiederholbar;
// manuell korrigierte Zuordnungen werden nie überschrieben.
app.MapPost("/debug/recategorize", async (CategorizationService categorization, CancellationToken ct) =>
{
    var (total, updated) = await categorization.RecategorizeAllAsync(ct);
    return Results.Ok(new
    {
        Message = $"Neu-Kategorisierung abgeschlossen: {updated}/{total} Kontoumsätze aktualisiert.",
        Total = total,
        Updated = updated,
    });
});

app.Run();

internal sealed record TanConfirmRequest(string? TanCode);
