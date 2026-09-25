-- Schema-Version 0009: Anlageklasse je Depot-Position (GitHub-Issue #4, KONZEPT.md Abschnitt 6
-- Phase 2). comdirect liefert das über instrument.staticData.instrumentType, wenn Positionen
-- mit with-attr=instrument abgefragt werden (bereits der Fall) - ein Enum-String
-- (SHARE/BONDS/SUBSCRIPTION_RIGHT/ETF/PROFIT_PART_CERTIFICATE/FUND/WARRANT/CERTIFICATE/
-- NOT_AVAILABLE laut offizieller Doku), keine freie DB-ENUM-Spalte, da comdirect diese Liste
-- jederzeit erweitern könnte und wir nicht bei jedem neuen Wert eine Migration brauchen wollen.
-- NULL möglich: ältere, bereits gespeicherte Positionen (vor dieser Migration) sowie Instrumente,
-- für die comdirect selbst keinen Typ liefert.
ALTER TABLE portfolio_positions
    ADD COLUMN instrument_type VARCHAR(32) NULL AFTER wkn;
