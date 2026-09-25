-- Schema-Version 0003: Gegenkonto-IBAN für Kontoumsätze (KONZEPT.md Abschnitt 6).
-- Primäres, zuverlässigeres Signal zur Erkennung interner Umbuchungen zwischen eigenen
-- Konten/Depots, ergänzt/ersetzt die reine Freitextsuche im Buchungstext.

ALTER TABLE transactions
    ADD COLUMN counterparty_iban VARCHAR(34) NULL AFTER transaction_type;
