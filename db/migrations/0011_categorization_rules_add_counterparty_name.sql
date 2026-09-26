-- Schema-Version 0011: neuer match_field-Wert für Kategorisierungsregeln, damit Regeln auch
-- gegen den neuen counterparty_name (0010) geprüft werden können, nicht nur gegen
-- BookingText/TransactionType (KONZEPT.md Abschnitt 6).
ALTER TABLE categorization_rules
    MODIFY COLUMN match_field ENUM('BookingText', 'TransactionType', 'CounterpartyName') NOT NULL;
