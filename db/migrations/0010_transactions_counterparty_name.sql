-- Schema-Version 0010: Empfänger-/Auftraggeber-Name für Kontoumsätze (KONZEPT.md Abschnitt 6).
-- comdirect liefert das bereits als holderName innerhalb von remitter/deptor/creditor - bisher
-- wurde nur die IBAN daraus übernommen (siehe
-- 0003_transactions_counterparty_iban.sql), der Name lag im geparsten DTO ungenutzt vor.
-- Ergänzt die bisherigen Kategorisierungsfelder (BookingText/TransactionType) um ein drittes
-- Signal für Buchungen, bei denen der Empfänger-Name nur strukturiert vorliegt, nicht im
-- Buchungstext (typisch bei echten Überweisungen, anders als bei Kartenzahlungen).
ALTER TABLE transactions
    ADD COLUMN counterparty_name VARCHAR(255) NULL AFTER counterparty_iban;
