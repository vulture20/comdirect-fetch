-- Schema-Version 0015: Guthaben des Standard-Verrechnungskontos und noch nicht gelieferte Käufe je
-- Snapshot (Issue #12, KONZEPT.md Abschnitt 13). Gesamtwert = total_value + settlement_cash +
-- securities_in_transit. Nötig, weil ein Kauf über das Verrechnungskonto das Guthaben sofort mindert,
-- die Wertpapiere aber erst zur Valuta im Depot stehen (live: Munich Re, Buchung 28.09., Valuta 30.09.).
-- Zusammen mit net_invested_capital (0014, Semantik jetzt: Startkapital + externe Bewegungen seit
-- Bezugspunkt) und time_weighted_return_pct werden alle Werte bei jedem Lauf für alle Snapshots ab dem
-- Bezugspunkt neu berechnet und bei Abweichung nachgetragen (selbstheilend, kein separates Backfill).
ALTER TABLE portfolio_snapshots
    ADD COLUMN settlement_cash        DECIMAL(18,2) NULL,
    ADD COLUMN securities_in_transit  DECIMAL(18,2) NULL;
