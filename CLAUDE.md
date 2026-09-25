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
  don't re-add either without the user asking). Both comdirect-fetch dashboards
  (`grafana/dashboards/salden.json`, uid `comdirect-salden`; `grafana/dashboards/depot.json`,
  uid `comdirect-depot`) live in this repo as their source of truth and are provisioned into
  the *existing* Grafana instance via its HTTP API (`POST /api/dashboards/db` with
  `overwrite: true`, datasource uid `comdirect-mariadb`), using a Grafana service-account
  token the user provided (Admin role — Editor role can't create datasources, that's a
  Grafana permission, not a bug). After editing either dashboard JSON, re-push it the same
  way rather than editing in the Grafana UI.

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
  `transactions` dedups via `INSERT IGNORE` on `(account_id, comdirect_reference)`; balance/
  snapshot tables are plain append-only inserts. **Dapper + enum parameters**: never pass a
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
  inserting new transactions). Each fetch service exposes a public `RunOnceAsync` in
  addition to its `BackgroundService` loop (which now runs once immediately on startup
  instead of waiting a full interval first) — `RunOnceAsync` is what `POST /debug/fetch-now`
  calls to trigger an immediate fetch without touching auth/session. `ComdirectAuthCoordinator`
  holds auth state in memory only — a restart always requires a fresh TAN approval via
  `POST /auth/start` then `POST /auth/confirm`. `GET /health` reports auth state and app
  version; `GET /debug/summary` reports row counts per table and the last 10 `sync_log`
  entries for quick verification without direct DB access.
- **`ComdirectFetch.Tests`** — xUnit; covers `CategorizationLogic`, `ComdirectResilience`
  (via a fake `HttpMessageHandler`, using a short-delay pipeline from `BuildPipeline` so
  retry tests don't wait on real backoff), and the banking DTO JSON quirks.

## Operator tooling

`scripts/comdirectctl.sh` (bash, needs `curl` + `jq`) wraps the TAN flow and status
endpoints for humans and scripts alike: `auth start`/`auth confirm`, `status` (add `--json`
for machine consumption), `fetch-now`, `recategorize`. Exit codes are meaningful (0 authenticated, 1 needs
attention, 2 unreachable, 3 missing deps, 64 bad usage) so it's usable in monitoring/cron,
not just interactively. If you touch this script, know the trap gotcha it already hit once:
under `set -e`, if the last command in an `EXIT` trap evaluates false (e.g. `[[ cond ]] &&
foo`), bash uses *that* exit status for the whole script, silently overriding an explicit
`exit N` earlier — write trap bodies as `if`/`fi` (which returns 0 on a false, no-else
condition), not `[[ ]] && ...`, to avoid this.

## Release automation

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
- Error message bodies from comdirect can appear with garbled umlauts in logs (cosmetic,
  root cause — likely a charset/encoding mismatch somewhere in the logging pipeline, not
  necessarily in the app itself — not yet investigated).
- Official docs live at `/opt/comdirect-fetch/docs` (Swagger, Postman collection, PDF spec)
  — check there first before guessing at API behavior, but confirm against a real request
  when in doubt: the docs have been wrong before (see above).
