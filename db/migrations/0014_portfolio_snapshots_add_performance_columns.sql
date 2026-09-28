-- Schema-Version 0014: um externe Ein-/Auszahlungen bereinigte Performance-Kennzahlen je
-- Snapshot (Issue #12, KONZEPT.md Abschnitt 6 Phase 4) - Netto-Kapitaleinsatz-Methode und
-- tagesverkettete Time-Weighted Return, beide berechnet von DepotPerformanceCalculator (Domain).
-- NULL für Snapshots vor v0.23.0 bzw. ohne erkannte Depot<->Verrechnungskonto-Verknüpfung - kein
-- rückwirkendes Backfill, matcht das Opt-in-Muster von TokenEncryptionKeyBase64/CredentialKeyFilePath.
ALTER TABLE portfolio_snapshots
    ADD COLUMN net_invested_capital     DECIMAL(18,2) NULL,
    ADD COLUMN dividends_received       DECIMAL(18,2) NULL,
    ADD COLUMN time_weighted_return_pct DECIMAL(10,4) NULL;
