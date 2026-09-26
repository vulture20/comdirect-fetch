using ComdirectFetch.Api;
using ComdirectFetch.Data;
using ComdirectFetch.Domain;
using ComdirectFetch.Worker;
using ComdirectFetch.Worker.Services;

var builder = WebApplication.CreateBuilder(args);

// KONZEPT.md Abschnitt 10 A: Docker-Compose-Secrets (Dateien unter /run/secrets/, z. B.
// "Comdirect__ClientId") als zusätzliche, nach den Env-Vars geladene Konfigurationsquelle -
// überschreibt gleichnamige Env-Var-Werte, fehlt das Verzeichnis/sind keine Secrets gemountet,
// ist das ein no-op (optional: true).
builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);

builder.Services.Configure<ComdirectApiOptions>(builder.Configuration.GetSection(ComdirectApiOptions.SectionName));
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.SectionName));
builder.Services.Configure<FetchOptions>(builder.Configuration.GetSection(FetchOptions.SectionName));
builder.Services.Configure<RetentionOptions>(builder.Configuration.GetSection(RetentionOptions.SectionName));
builder.Services.Configure<NotificationOptions>(builder.Configuration.GetSection(NotificationOptions.SectionName));
builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection(AdminOptions.SectionName));

builder.Services.AddSingleton<ComdirectRequestContext>();
builder.Services.AddSingleton<CredentialProvider>();
builder.Services.AddSingleton<ICredentialProvider>(sp => sp.GetRequiredService<CredentialProvider>());
builder.Services.AddHttpClient<ComdirectAuthClient>();
builder.Services.AddHttpClient<ComdirectBankingClient>();
builder.Services.AddHttpClient<ComdirectBrokerageClient>();
builder.Services.AddSingleton<ComdirectAuthCoordinator>();

// GitHub-Issue #6: Benachrichtigungskanäle, beide opt-in und gleichzeitig nutzbar ("auch
// parallel"). Als IEnumerable<INotificationChannel> registriert, damit NotificationService alle
// aktivierten Kanäle findet, ohne sie einzeln zu kennen.
builder.Services.AddSingleton<EmailNotificationChannel>();
builder.Services.AddSingleton<INotificationChannel>(sp => sp.GetRequiredService<EmailNotificationChannel>());
builder.Services.AddHttpClient<WebhookNotificationChannel>();
builder.Services.AddSingleton<INotificationChannel>(sp => sp.GetRequiredService<WebhookNotificationChannel>());
builder.Services.AddSingleton<NotificationService>();

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
builder.Services.AddSingleton<CredentialRepository>();
builder.Services.AddSingleton<RetentionRepository>();
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
builder.Services.AddSingleton<RetentionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RetentionService>());

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

// GitHub-Issue #13, KONZEPT.md Abschnitt 12: /admin/rules/* (Web-Oberfläche + zugehörige API für
// Kategorien/Regeln) ist bewusst strenger geschützt als die übrigen, unauthentifizierten
// /debug/*-/auth/*-Endpunkte (und auch /admin/credentials, Issue #10 B - anderer Pfad, andere
// Vertrauensebene), da hier dauerhafte Konfiguration geändert wird, nicht nur eine Aktion
// angestoßen. Ohne gesetztes Admin__Password bleibt /admin/rules/* komplett deaktiviert (503)
// statt ungeschützt erreichbar.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/admin/rules"))
    {
        var adminPassword = context.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<AdminOptions>>().Value.Password;
        if (string.IsNullOrEmpty(adminPassword))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new { Message = "Admin__Password nicht konfiguriert - /admin/rules/* ist deaktiviert." });
            return;
        }

        if (!AdminAuth.TryValidate(context.Request.Headers.Authorization.ToString(), adminPassword))
        {
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"comdirect-fetch admin\"";
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { Message = "Nicht autorisiert." });
            return;
        }
    }

    await next(context);
});
app.UseStaticFiles();

app.MapGet("/admin/rules", () => Results.Redirect("/admin/rules/index.html"));

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

// GitHub-Issue #13, KONZEPT.md Abschnitt 12: Kategorien-CRUD für die neue Web-Oberfläche.
// Geschützt durch die /admin/rules-Middleware oben.
app.MapGet("/admin/rules/api/categories", async (CategoryRepository categories, CancellationToken ct) =>
    Results.Ok((await categories.GetAllAsync(ct)).Select(c => new { c.Id, c.Name, Type = c.Type.ToString() })));

app.MapPost("/admin/rules/api/categories", async (CategoryRepository categories, CategoryRequest body, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.Name) || !Enum.TryParse<CategoryType>(body.Type, out var type))
    {
        return Results.BadRequest(new { Message = "name und ein gültiger type (Einnahme/Ausgabe/InternNeutral) sind erforderlich." });
    }

    var id = await categories.CreateAsync(new Category { Name = body.Name, Type = type }, ct);
    return Results.Ok(new { Id = id });
});

app.MapPut("/admin/rules/api/categories/{id:long}", async (long id, CategoryRepository categories, CategoryRequest body, CancellationToken ct) =>
{
    var existing = await categories.GetByIdAsync(id, ct);
    if (existing is null)
    {
        return Results.NotFound();
    }

    if (ProtectedCategoryNames.All.Contains(existing.Name) && !string.Equals(existing.Name, body.Name, StringComparison.Ordinal))
    {
        return Results.BadRequest(new { Message = $"'{existing.Name}' ist eine besondere Kategorie und kann nicht umbenannt werden." });
    }

    if (string.IsNullOrWhiteSpace(body.Name) || !Enum.TryParse<CategoryType>(body.Type, out var type))
    {
        return Results.BadRequest(new { Message = "name und ein gültiger type (Einnahme/Ausgabe/InternNeutral) sind erforderlich." });
    }

    await categories.UpdateAsync(new Category { Id = id, Name = body.Name, Type = type }, ct);
    return Results.Ok();
});

app.MapDelete("/admin/rules/api/categories/{id:long}", async (long id, CategoryRepository categories, CancellationToken ct) =>
{
    var existing = await categories.GetByIdAsync(id, ct);
    if (existing is null)
    {
        return Results.NotFound();
    }

    if (ProtectedCategoryNames.All.Contains(existing.Name))
    {
        return Results.BadRequest(new { Message = $"'{existing.Name}' ist eine besondere Kategorie und kann nicht gelöscht werden." });
    }

    try
    {
        await categories.DeleteAsync(id, ct);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { Message = $"Löschen fehlgeschlagen (wird die Kategorie noch von Regeln oder Umsätzen referenziert?): {ex.Message}" });
    }

    return Results.Ok();
});

// GitHub-Issue #13, KONZEPT.md Abschnitt 12: Regel-CRUD für die neue Web-Oberfläche, inkl.
// "weicher" Prioritäts-Kollisionswarnung (kein Hard-Block).
app.MapGet("/admin/rules/api/rules", async (CategorizationRuleRepository rules, CategoryRepository categories, CancellationToken ct) =>
{
    var allRules = await rules.GetAllOrderedByPriorityAsync(ct);
    var categoryNames = (await categories.GetAllAsync(ct)).ToDictionary(c => c.Id, c => c.Name);
    return Results.Ok(allRules.Select(r => new
    {
        r.Id,
        r.Pattern,
        MatchField = r.MatchField.ToString(),
        r.CategoryId,
        CategoryName = categoryNames.GetValueOrDefault(r.CategoryId),
        r.Priority,
    }));
});

app.MapPost("/admin/rules/api/rules", async (CategorizationRuleRepository rules, RuleRequest body, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.Pattern) || !Enum.TryParse<RuleMatchField>(body.MatchField, out var matchField) || body.CategoryId is null)
    {
        return Results.BadRequest(new { Message = "pattern, matchField (BookingText/TransactionType) und categoryId sind erforderlich." });
    }

    var priorityCollision = await rules.PriorityInUseAsync(body.Priority, excludeId: null, ct);
    var id = await rules.CreateAsync(new CategorizationRule { Pattern = body.Pattern, MatchField = matchField, CategoryId = body.CategoryId.Value, Priority = body.Priority }, ct);
    return Results.Ok(new { Id = id, PriorityCollision = priorityCollision });
});

app.MapPut("/admin/rules/api/rules/{id:long}", async (long id, CategorizationRuleRepository rules, RuleRequest body, CancellationToken ct) =>
{
    var existing = await rules.GetByIdAsync(id, ct);
    if (existing is null)
    {
        return Results.NotFound();
    }

    if (string.IsNullOrWhiteSpace(body.Pattern) || !Enum.TryParse<RuleMatchField>(body.MatchField, out var matchField) || body.CategoryId is null)
    {
        return Results.BadRequest(new { Message = "pattern, matchField (BookingText/TransactionType) und categoryId sind erforderlich." });
    }

    var priorityCollision = await rules.PriorityInUseAsync(body.Priority, excludeId: id, ct);
    await rules.UpdateAsync(new CategorizationRule { Id = id, Pattern = body.Pattern, MatchField = matchField, CategoryId = body.CategoryId.Value, Priority = body.Priority }, ct);
    return Results.Ok(new { PriorityCollision = priorityCollision });
});

app.MapDelete("/admin/rules/api/rules/{id:long}", async (long id, CategorizationRuleRepository rules, CancellationToken ct) =>
{
    await rules.DeleteAsync(id, ct);
    return Results.Ok();
});

// GitHub-Issue #13, KONZEPT.md Abschnitt 12: Testen gegen Echtdaten, Ebene 1 - Einzel-Regel-
// Vorschau. Rein lesend: prüft nur, ob das Muster auf das jeweilige Feld passt (wie
// CategorizationLogic.Categorize es täte), unabhängig von Priorität/anderen Regeln - reicht für
// die Kernfrage "matcht dieses Muster wirklich nur das, was ich meine?".
app.MapPost("/admin/rules/api/rules/preview", async (
    RulePreviewRequest body,
    TransactionRepository transactions,
    CategoryRepository categories,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.Pattern) || !Enum.TryParse<RuleMatchField>(body.MatchField, out var matchField))
    {
        return Results.BadRequest(new { Message = "pattern und matchField (BookingText/TransactionType/CounterpartyName) sind erforderlich." });
    }

    var categoryNames = (await categories.GetAllAsync(ct)).ToDictionary(c => c.Id, c => c.Name);
    var matched = (await transactions.GetAllAsync(ct))
        .Where(t =>
        {
            var haystack = matchField switch
            {
                RuleMatchField.TransactionType => t.TransactionType,
                RuleMatchField.CounterpartyName => t.CounterpartyName,
                _ => t.BookingText,
            };
            return haystack is not null && haystack.Contains(body.Pattern, StringComparison.OrdinalIgnoreCase);
        })
        .ToList();

    return Results.Ok(new
    {
        TotalMatches = matched.Count,
        Matches = matched.Take(50).Select(t => new
        {
            t.Id,
            t.BookingText,
            t.TransactionType,
            t.CounterpartyName,
            t.Amount,
            CurrentCategory = t.CategoryId is { } catId ? categoryNames.GetValueOrDefault(catId) : null,
        }),
    });
});

// GitHub-Issue #13, KONZEPT.md Abschnitt 12: Testen gegen Echtdaten, Ebene 2 - volle Simulation
// des aktuell gespeicherten Regelsatzes gegen alle nicht manuell kategorisierten Umsätze, ohne
// zu schreiben. Bestätigen im Frontend löst das bereits bestehende /debug/recategorize aus, das
// dann tatsächlich schreibt.
app.MapPost("/admin/rules/api/rules/simulate", async (
    CategorizationService categorization,
    CategoryRepository categories,
    CancellationToken ct) =>
{
    var diff = await categorization.SimulateRecategorizationAsync(ct);
    var categoryNames = (await categories.GetAllAsync(ct)).ToDictionary(c => c.Id, c => c.Name);
    string? NameOf(long? id) => id is { } v ? categoryNames.GetValueOrDefault(v) : null;

    return Results.Ok(new
    {
        ChangedCount = diff.Count,
        Changes = diff.Select(d => new
        {
            d.Transaction.Id,
            d.Transaction.BookingText,
            d.Transaction.Amount,
            OldCategory = NameOf(d.OldCategoryId),
            NewCategory = NameOf(d.NewCategoryId),
        }),
    });
});

// KONZEPT.md Abschnitt 10 B: Bootstrap-Schritt für Zugangsnummer/PIN - verschlüsselt sie mit
// dem dedizierten, dateibasierten Schlüssel (Comdirect__CredentialKeyFilePath) und legt sie in
// credential_store ab. Danach können Comdirect__Username/Comdirect__Password aus .env entfernt
// werden. Keine zusätzliche Auth (gleiche Vertrauensebene wie /auth/*, /debug/* - Dienst ist
// nur host-lokal auf diesem Port erreichbar). Schlägt fehl, wenn keine Schlüsseldatei
// konfiguriert ist (siehe CredentialProvider.SetCredentialsAsync).
app.MapPost("/admin/credentials", async (CredentialProvider credentials, SetCredentialsRequest body, CancellationToken ct) =>
{
    if (string.IsNullOrEmpty(body.Username) || string.IsNullOrEmpty(body.Password))
    {
        return Results.BadRequest(new { Message = "username und password sind erforderlich." });
    }

    try
    {
        await credentials.SetCredentialsAsync(body.Username, body.Password, ct);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { Message = ex.Message });
    }

    return Results.Ok(new { Message = "Zugangsnummer/PIN verschlüsselt gespeichert. .env kann jetzt bereinigt werden." });
});

// KONZEPT.md Abschnitt 11: stößt Konsolidierung/Aufräumen sofort an, statt auf das konfigurierte
// Intervall zu warten. Komplett opt-in (siehe RetentionOptions) - ohne gesetzte Retention__*-
// Zeiträume ist das ein no-op. Berührt weder Session noch TAN, gefahrlos wiederholbar.
app.MapPost("/debug/consolidate", async (RetentionService retention, CancellationToken ct) =>
{
    await retention.RunOnceAsync(ct);
    return Results.Ok(new { Message = "Konsolidierung/Aufräumen angestoßen, siehe sync_log für Details." });
});

// GitHub-Issue #6: löst eine Testbenachrichtigung über alle aktivierten Kanäle aus, ohne dafür
// eine echte Session-Störung abwarten zu müssen. Berührt weder Session noch TAN.
app.MapPost("/debug/notify-test", async (NotificationService notifications, CancellationToken ct) =>
{
    await notifications.NotifyAuthRequiredAsync("Dies ist eine Testbenachrichtigung (POST /debug/notify-test).", ct);
    return Results.Ok(new { Message = "Testbenachrichtigung an alle aktivierten Kanäle angestoßen, siehe Logs für Details." });
});

app.Run();

internal sealed record TanConfirmRequest(string? TanCode);
internal sealed record SetCredentialsRequest(string? Username, string? Password);
internal sealed record CategoryRequest(string? Name, string? Type);
internal sealed record RuleRequest(string? Pattern, string? MatchField, long? CategoryId, int Priority);
internal sealed record RulePreviewRequest(string? Pattern, string? MatchField);
