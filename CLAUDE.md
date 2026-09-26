# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A C# service that polls the comdirect REST API on configurable intervals (account balances,
portfolio overview, account transactions), historizes the data into a MariaDB the user
provides externally, and runs in Docker. Full concept — architecture, data model, planned
evaluations, open questions — lives in `docs/konzept.md`; read it before making
architectural changes, it's the source of truth this code was built from.

## Commands

```bash
dotnet build                                                # build the whole solution
dotnet test                                                 # run all tests
dotnet test --filter FullyQualifiedName~CategorizationLogicTests   # run a single test class
docker compose -f docker/docker-compose.yml up --build       # run the fetch service (only service in the compose file)
```

There's no separate lint step; `dotnet build` surfaces nullable-reference and compiler warnings.

## This host's environment — read before touching ports or Grafana

This is a shared homelab host running many unrelated Docker containers (dozens, `docker ps`
to see them) — not an isolated sandbox. Two things that already bit us once:

- **Port 8080 is taken by something else on this host**; the fetch service runs on **8750**
  instead (`docker/docker-compose.yml`). Always check `docker ps` / `ss -tlnp` for conflicts
  before picking a host port for anything new here.
- **There's already a Grafana instance on this host** (container name `grafana`, port 3000,
  org "BugZone"). This project does **not** run its own Grafana container — there's no
  `grafana` service in `docker/docker-compose.yml` and no `grafana/provisioning/` directory
  (an earlier "self-contained deployment" version of both existed briefly and was removed
  again as speculative complexity for a hypothetical fresh install without existing Grafana —
  don't re-add either without the user asking). All four comdirect-fetch dashboards
  (`grafana/dashboards/{salden,depot,cashflow,depot-performance}.json`, uids
  `comdirect-{salden,depot,cashflow,depot-performance}`) live in this repo as their source of
  truth and are provisioned into the *existing* Grafana instance, plus the MySQL/MariaDB
  datasource (uid `comdirect-mariadb`), via `scripts/grafana-setup.sh` (Issue #9) — host-only
  tooling like `comdirectctl.sh` (not baked into the Docker image, no `AppVersion` bump needed).
  Idempotent; `GRAFANA_TOKEN` (a Grafana service-account token, Admin role — Editor can't create
  datasources, that's a Grafana permission, not a bug) is required, `GRAFANA_URL` defaults to
  `http://localhost:3000`, DB connection details for the datasource come from the local `.env`.
  Replaces the earlier manual/ad-hoc `POST /api/dashboards/db` calls. After editing a dashboard
  JSON, re-run `./scripts/grafana-setup.sh dashboards` rather than editing in the Grafana UI.

## Architecture

Five projects under `src/`, referencing each other in one direction only
(`Worker` → `Api`/`Data` → `Domain`; nothing references `Worker`):

- **`ComdirectFetch.Domain`** — plain POCOs mirroring the DB tables (`Account`,
  `Transaction`, `SyncLogEntry`, …), enums, `AppVersion` (the single source of truth for
  the app's SemVer), and `CategorizationLogic` — the categorization algorithm as a pure,
  DB-free static function so it's unit-testable without mocking repositories.
- **`ComdirectFetch.Api`** — comdirect REST client: `ComdirectAuthClient` implements the
  multi-step OAuth2 + TAN flow (initial token → session → TAN challenge → activate →
  `cd_secondary` token exchange → refresh); `ComdirectBankingClient`/`ComdirectBrokerageClient`
  fetch balances/transactions/depots/positions. Endpoint paths and JSON field names were
  cross-checked against the official Swagger/Postman collection/PDF spec the user placed at
  `/opt/comdirect-fetch/docs`, **and then live-tested end-to-end with real credentials**
  (see `CHANGELOG.md` 0.2.0–0.7.0): login/session/TAN, balances, portfolio overview, and
  paginated transactions all confirmed working. Several real API quirks the docs got wrong
  were found and fixed this way — e.g. `bookingDate` is a plain string in practice (not the
  documented nested `{"date": ...}` object; `FlexibleDateConverter` now accepts both), and
  `paging-first > 0` needs `transactionState=BOOKED` explicitly or comdirect returns 422.
  When something about the API doesn't behave as documented, trust a live test over the
  docs — use `POST /debug/fetch-now` and `GET /debug/summary` (see below) to check quickly.
  **Safety-critical**: comdirect locks the entire online banking access (not just API
  access) after three wrong TAN entries or five unredeemed TAN challenges — never call
  `POST /auth/start` in a retry loop; `ComdirectAuthCoordinator.StartAsync` already returns
  the existing pending challenge instead of requesting a new one, but that's not a full
  guard against external retries. **Rate limiting (HTTP 429)**, live-observed under heavy
  testing, is handled by `ComdirectResilience` (Polly retry with exponential backoff +
  jitter, respects `Retry-After`) — deliberately wired into the token endpoints and the
  banking/brokerage data endpoints only, **not** into `RequestTanChallengeAsync` or
  `ActivateSessionAsync`, since auto-retrying those could create an unwanted extra TAN
  challenge or resubmit a TAN code. If you add a new comdirect call, decide consciously
  whether it's "safe to retry" (token/data, idempotent) or "TAN-sensitive" (session
  validate/activate) before wiring it through `ComdirectResilience.SendWithRetryAsync`.
- **`ComdirectFetch.Data`** — Dapper + MySqlConnector repositories (one per table) and
  `DatabaseMigrator`, which runs the DbUp-based migration on startup against the scripts in
  `db/migrations/` (embedded into the assembly via the `.csproj`, not copied at runtime).
  `transactions` dedups via an upsert on `(account_id, comdirect_reference)` (v0.17.0: changed
  from plain `INSERT IGNORE` to `ON DUPLICATE KEY UPDATE counterparty_name = ...` — comdirect
  re-sends its whole available history on every fetch with no incremental cursor, so this
  backfills `counterparty_name` for already-stored rows the next time they come back around;
  `category_id`/`manually_categorized` are never touched on conflict, so this can't clobber
  existing categorization); balance/snapshot tables are plain append-only inserts. **Dapper +
  enum parameters**: never pass a
  C# enum property directly as a Dapper parameter against a MySQL `ENUM` column — Dapper
  reduces enum parameters to their underlying numeric type internally before any registered
  `SqlMapper.TypeHandler<T>` gets a chance to run, so the handler is silently ineffective and
  the insert fails with "Data truncated for column". Convert with `.ToString()` in the
  parameter object instead (see `SyncLogRepository`). Enum *columns* read back into enum
  *properties* work fine without any of this — it's a parameter-binding-only gotcha.
- **`ComdirectFetch.Worker`** — the entry point (`Program.cs`, ASP.NET Core minimal hosting,
  everything registered as singletons since repositories are stateless). Background
  services: `TokenRefreshBackgroundService` (keeps the session alive via refresh, runs far
  more often than the data-fetch intervals), `BalanceFetchService`,
  `PortfolioFetchService`, `TransactionFetchService` (calls `CategorizationService` after
  inserting new transactions), `RetentionService` (v0.13.0, see below). Each fetch/maintenance
  service exposes a public `RunOnceAsync` in addition to its `BackgroundService` loop (which now
  runs once immediately on startup instead of waiting a full interval first) — `RunOnceAsync` is
  what the matching `POST /debug/*` endpoint calls to trigger an immediate run without touching
  auth/session. `ComdirectAuthCoordinator` keeps auth state in memory, with optional encrypted
  persistence across restarts (v0.11.0, see below) — see "Known gaps" for exact behavior. On a
  genuine transition to `NichtAuthentifiziert` (session lost, no valid persisted token to
  restore), `NotificationService` (v0.14.0, Issue #6) fires once via whatever
  `Notification__*` channels (email/webhook, both usable in parallel) are configured — opt-in,
  no-op if unconfigured. `GET /health` reports auth state and app version; `GET /debug/summary`
  reports row counts per table and the last 10 `sync_log` entries for quick verification without
  direct DB access.
- **`ComdirectFetch.Tests`** — xUnit; covers `CategorizationLogic`, `ComdirectResilience`
  (via a fake `HttpMessageHandler`, using a short-delay pipeline from `BuildPipeline` so
  retry tests don't wait on real backoff), and the banking DTO JSON quirks.

## Operator tooling

`scripts/comdirectctl.sh` (bash, needs `curl` + `jq` + `openssl`) wraps the TAN flow and status
endpoints for humans and scripts alike: `auth start`/`auth confirm`, `status` (add `--json`
for machine consumption), `fetch-now`, `recategorize`, `set-credentials` (§10 B bootstrap),
`generate-token-key`/`generate-bootstrap-key` (random-key helpers for
`Comdirect__TokenEncryptionKeyBase64` and the §10 B bootstrap key file — both pure local
commands, never touch the running service; `generate-bootstrap-key` refuses to overwrite an
existing key file, since that would orphan whatever is already encrypted in `credential_store`).
Exit codes are meaningful (0 authenticated, 1 needs
attention, 2 unreachable, 3 missing deps, 64 bad usage) so it's usable in monitoring/cron,
not just interactively. **Not baked into the Docker image** (`docker/Dockerfile` never copies
`scripts/`) — it's host-only tooling, so changes to it don't need an `AppVersion` bump or a
release tag. If you touch this script, know the trap gotcha it already hit once:
under `set -e`, if the last command in an `EXIT` trap evaluates false (e.g. `[[ cond ]] &&
foo`), bash uses *that* exit status for the whole script, silently overriding an explicit
`exit N` earlier — write trap bodies as `if`/`fi` (which returns 0 on a false, no-else
condition), not `[[ ]] && ...`, to avoid this.

## CI and release automation

`.github/workflows/ci.yml` (Issue #10): `dotnet build` + `dotnet test` on every push to any
branch and every PR against `main` — independent of the release workflow below, so a broken
build/test surfaces immediately instead of only at the next release tag. Builds no Docker
image, publishes nothing. `push` is scoped to `branches: ["**"]` specifically so a tag push
doesn't *also* trigger this workflow redundantly alongside `docker-release.yml`'s own gate.
Pure repo/CI tooling like `scripts/comdirectctl.sh` — not part of the Docker image, no
`AppVersion` bump or release tag needed for changes to it.

`.github/workflows/docker-release.yml`: on every pushed `vX.Y.Z` tag, runs `dotnet build`
+ `dotnet test` as a gate, then builds the Docker image and pushes it to
`ghcr.io/vulture20/comdirect-fetch` tagged with the version **and** `latest`. No extra
secrets — uses the built-in `GITHUB_TOKEN`. Also has a `workflow_dispatch` trigger for
manual test runs; those deliberately do *not* touch `latest` (tagged `manual-<run number>`
instead) so a manual test can't accidentally become the "current" image. When you cut a
release, tag with `git tag -a vX.Y.Z` matching `AppVersion.Current` and `git push origin
vX.Y.Z` — that's what fires this.

## Data model and schema versioning

`db/migrations/*.sql` is the only source of truth for the DB schema and is **append-only**:
never edit an existing migration, always add the next `NNNN_description.sql`. DbUp tracks
which scripts already ran in the target database itself. `0001_init.sql` creates the
tables; `0002_seed_categories.sql` seeds the starter categorization categories/rules
described in `docs/konzept.md` §6.

## Versioning (from docs/konzept.md §8) — do this automatically, on every change

- **App version** (SemVer, `ComdirectFetch.Domain.AppVersion.Current`): bump on every
  behavior-changing change (PATCH = fix, MINOR = new backward-compatible functionality,
  MAJOR = breaking change) and add an entry to `CHANGELOG.md`.
- **DB schema version**: any structural change gets a new, sequentially numbered file in
  `db/migrations/` — never edit an existing one.

Do this as part of the change itself, not only when the user asks for it.

## Known gaps (see docs/konzept.md §9 for the full list)

- `ComdirectAuthCoordinator` state now survives restarts when `Comdirect__TokenEncryptionKeyBase64`
  is set (v0.11.0): the current token is AES-256-GCM encrypted (`ComdirectFetch.Domain.SecretEncryption`)
  and stored via `ComdirectFetch.Data.AuthTokenRepository` in `auth_token_store`
  (`db/migrations/0006_auth_token_store.sql`), restored via `ComdirectAuthCoordinator.TryRestoreAsync`
  in `Program.cs` before `app.Run()`. Without that env var set, behavior is unchanged
  (in-memory only, fresh TAN approval needed on every restart) — it's opt-in, and only the
  encrypted blob is stored, never the key itself.
- Rate limiting has real retry/backoff now (see above) but is untested at production data
  volumes (large depots, many accounts, long transaction history).
- Umlaut garbling in logged comdirect error bodies (Issue #8) is fixed. Root cause confirmed by
  reproduction (not guessed): comdirect's error response bytes are actually Latin-1/ISO-8859-1
  encoded with no reliable charset header, but .NET's default `ReadAsStringAsync` assumes UTF-8
  when no charset is given and silently replaces invalid byte sequences with U+FFFD (`�`) instead
  of erroring or trying another encoding — confirmed by feeding real Latin-1 bytes for
  "überschritten" through .NET's actual decoder and reproducing the exact `�berschritten`
  symptom from the issue. Fix: `ComdirectFetch.Domain.TextDecoding.DecodeUtf8WithLatin1Fallback`
  (pure, unit-tested) tries strict UTF-8 first and only falls back to Latin-1 on an actual decode
  failure, so genuinely UTF-8 responses are unaffected. Used in
  `HttpResponseExtensions.EnsureSuccessWithBodyAsync` (Api) instead of the default
  `ReadAsStringAsync` charset handling.
- The four comdirect credentials now get better-than-plaintext-in-.env handling (v0.12.0,
  `docs/konzept.md` §10, distinct from the session-token persistence above — that's the token,
  this is the login credentials themselves). ClientId/ClientSecret: Docker Compose file secrets
  (`docker/docker-compose.yml` `secrets:` block, files under `secrets/` — gitignored, must exist
  before `docker compose up`), read via the shared-framework `Microsoft.Extensions.Configuration.
  KeyPerFile` provider (`AddKeyPerFile("/run/secrets", optional: true)` in `Program.cs`) instead of
  `env_file`. Username/Password: bootstrap-once-then-wipe — `POST /admin/credentials` /
  `comdirectctl.sh set-credentials` (PIN via `read -s`, never a CLI arg) encrypts them
  (`ComdirectFetch.Worker.Services.CredentialProvider`, AES-256-GCM via the same
  `SecretEncryption` as the token but a **separate, dedicated** key — never
  `TokenEncryptionKeyBase64`) into a new `credential_store` table
  (`db/migrations/0007_credential_store.sql`, `ComdirectFetch.Data.CredentialRepository`, same
  shape as `auth_token_store`). That key lives in its own file
  (`Comdirect__CredentialKeyFilePath`, default `/run/secrets/credential_key`, bind-mounted from
  outside `.env` and outside the compose project dir — deliberately not in `.env`, or wiping
  Username/Password from `.env` would buy nothing). `ComdirectFetch.Api.ICredentialProvider`
  decouples `ComdirectAuthClient` from `ComdirectApiOptions.Username/Password` so the value is
  resolved at call time, not at DI-container-build time. Everything here is opt-in with a
  transparent fallback to `.env`/`ComdirectApiOptions` if the key file or DB row is absent — no
  forced migration for deployments that don't set this up.
- Retention/cleanup (v0.13.0, `docs/konzept.md` §11): `ComdirectFetch.Worker.Services.
  RetentionService` (`BackgroundService`, daily, also `POST /debug/consolidate` /
  `comdirectctl.sh consolidate`) consolidates `account_balances`/`portfolio_snapshots` (+
  `portfolio_positions`) down to one row/day once a row's day is older than
  `Retention__RawDataRetentionDays`, keeping the day's last value (`ComdirectFetch.Data.
  RetentionRepository` — cross-cutting like `DiagnosticsRepository`, deliberately not
  "one repo per table"); optionally fully deletes already-consolidated rows after an additional
  `Retention__ConsolidatedDataRetentionDays`. `sync_log` gets its own simpler age-based deletion
  via `Retention__SyncLogRetentionDays`, no consolidation stage. `transactions` is never touched
  — it's the financial ledger. All three thresholds are opt-in (unset = today's forever-keep
  behavior, matching the `TokenEncryptionKeyBase64`/`CredentialKeyFilePath` pattern); each run
  logs to `sync_log` (new `data_kind` `Konsolidierung`,
  `db/migrations/0008_sync_log_add_konsolidierung.sql`) with a row-count summary in
  `error_message` even on success (pragmatic reuse of that column instead of a schema change).
  No DB transactions (matches the rest of this codebase) — all operations are idempotent, so a
  crash mid-run just gets caught up by the next run. Live-verified against the real DB using
  synthetic rows dated years outside the real data's range, confirming both consolidation and
  deletion work correctly and real data is never touched.
- UI/CRUD for `categories`/`categorization_rules` (v0.16.0, `docs/konzept.md` §12, Issue #13):
  small worker-hosted web admin page at `wwwroot/admin/rules/index.html`, served + protected under
  `/admin/rules/*` — deliberately **not** bare `/admin/*`, since that collides with the existing,
  intentionally-unauthenticated `POST /admin/credentials` (§10 B, different trust level). Table
  editor calling new CRUD endpoints (`CategoryRepository`/`CategorizationRuleRepository` gained
  `CreateAsync`/`UpdateAsync`/`DeleteAsync`/`GetByIdAsync`, previously read-only). Real-data
  dry-run: `POST /admin/rules/api/rules/preview` (single candidate pattern vs. real transactions,
  independent of priority) and `POST /admin/rules/api/rules/simulate`
  (`CategorizationService.SimulateRecategorizationAsync` — same computation as
  `RecategorizeAllAsync` minus the write, full diff), both reusing the already-pure
  `CategorizationLogic.Categorize`. HTTP Basic Auth via `Admin__Password`
  (`ComdirectFetch.Worker.AdminAuth`, unit-tested, constant-time compare) — unset means
  `/admin/rules/*` returns 503, not unprotected; deliberately stricter than the unauthenticated
  `/debug/*`/`/auth/*`/`/admin/credentials` endpoints since this writes durable config.
  `ComdirectFetch.Domain.ProtectedCategoryNames` (`Intern/Neutral`, `Sonstige Einnahme`,
  `Sonstige Ausgabe`) is checked server-side to block deleting/renaming those three — shared
  between `CategorizationService` and the new endpoints so they can't drift. v0.17.0 added a
  third `RuleMatchField.CounterpartyName` (matches `transactions.counterparty_name`, sourced
  from comdirect's `remitter`/`deptor`/`creditor.holderName` — always fetched, previously
  discarded; `TransactionRepository.InsertIfNewAsync` is now an upsert that backfills just that
  column on conflict, never touching `category_id`/`manually_categorized`). v0.18.0 added
  `GET /admin/rules/api/uncategorized` (transactions with no category or only the sign-based
  fallback, for finding rule candidates) plus a Grafana dashboard link on `cashflow.json`
  pointing at `/admin/rules/`.
- Official docs live at `/opt/comdirect-fetch/docs` (Swagger, Postman collection, PDF spec)
  — check there first before guessing at API behavior, but confirm against a real request
  when in doubt: the docs have been wrong before (see above).
