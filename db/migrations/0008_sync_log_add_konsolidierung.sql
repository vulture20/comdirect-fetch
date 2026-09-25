-- Schema-Version 0008: neuer sync_log.data_kind-Wert für den Konsolidierungs-/Aufräumprozess
-- (docs/konzept.md Abschnitt 11). ENUM-Erweiterung statt neuer Tabelle, da sync_log bereits
-- der etablierte Ort für "ein Lauf, ein Protokolleintrag" ist (siehe TokenRefresh/Salden/
-- Depotuebersicht/Kontoumsaetze).
ALTER TABLE sync_log
    MODIFY COLUMN data_kind ENUM('TokenRefresh', 'Salden', 'Depotuebersicht', 'Kontoumsaetze', 'Konsolidierung') NOT NULL;
