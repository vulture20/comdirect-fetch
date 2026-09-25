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
builder.Services.AddSingleton<CategorizationService>();

builder.Services.AddHostedService<TokenRefreshBackgroundService>();
builder.Services.AddHostedService<BalanceFetchService>();
builder.Services.AddHostedService<PortfolioFetchService>();
builder.Services.AddHostedService<TransactionFetchService>();

var app = builder.Build();

// Datenbank-Schema beim Start auf den neuesten Stand bringen (KONZEPT.md Abschnitt 8:
// append-only Migrationen aus db/migrations/, DbUp führt die Historie in der DB selbst).
using (var scope = app.Services.CreateScope())
{
    var dbOptions = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DatabaseOptions>>().Value;
    scope.ServiceProvider.GetRequiredService<DatabaseMigrator>().MigrateToLatest(dbOptions.BuildConnectionString());
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

app.Run();

internal sealed record TanConfirmRequest(string? TanCode);
