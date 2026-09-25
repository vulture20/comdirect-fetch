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
dotnet build                                          # build the whole solution
dotnet test                                            # run all tests
dotnet test --filter FullyQualifiedName~CategorizationLogicTests   # run a single test class
docker compose -f docker/docker-compose.yml up --build # run fetch service (+ optional Grafana)
```

There's no separate lint step; `dotnet build` surfaces nullable-reference and compiler warnings.

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
  fetch balances/transactions/depots/positions. **The exact endpoint paths and JSON field
  names here are reconstructed from community sources, not verified against the official
  Swagger/Postman collection** — expect to adjust `BankingModels.cs`/`BrokerageModels.cs`/
  `SessionModels.cs` and the client paths against real responses.
- **`ComdirectFetch.Data`** — Dapper + MySqlConnector repositories (one per table) and
  `DatabaseMigrator`, which runs the DbUp-based migration on startup against the scripts in
  `db/migrations/` (embedded into the assembly via the `.csproj`, not copied at runtime).
  `transactions` dedups via `INSERT IGNORE` on `(account_id, comdirect_reference)`; balance/
  snapshot tables are plain append-only inserts.
- **`ComdirectFetch.Worker`** — the entry point (`Program.cs`, ASP.NET Core minimal hosting,
  everything registered as singletons since repositories are stateless). Background
  services: `TokenRefreshBackgroundService` (keeps the session alive via refresh, runs far
  more often than the data-fetch intervals), `BalanceFetchService`,
  `PortfolioFetchService`, `TransactionFetchService` (calls `CategorizationService` after
  inserting new transactions). `ComdirectAuthCoordinator` holds auth state in memory only —
  a restart always requires a fresh TAN approval via `POST /auth/start` then
  `POST /auth/confirm`. `GET /health` reports auth state and app version.
- **`ComdirectFetch.Tests`** — xUnit; currently covers `CategorizationLogic` only.

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

- comdirect API endpoint paths/payloads: unverified, see above.
- Transaction dedup key assumes comdirect's `reference` is stable and unique per account;
  unconfirmed against official docs.
- No pagination handling yet for `GetTransactionsAsync` (marked with a `TODO` in
  `ComdirectBankingClient`).
- `ComdirectAuthCoordinator` state is in-memory only (no persistence across restarts).
