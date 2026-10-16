-- Schema-Version 0017: Depot-Transaktionen (Käufe/Verkäufe je Wertpapier) für die Gebührenübersicht
-- (Issue #19, KONZEPT.md Abschnitt 17). comdirect weist Handelsgebühren nicht als eigene Kontobuchung
-- aus; GET /brokerage/v3/depots/{depotId}/transactions liefert aber je Trade Stückzahl,
-- Ausführungskurs und transactionValue. Live geprüft: transactionValue ist der Abrechnungsbetrag inkl.
-- Gebühren (centgenau gleich der Kontobuchung), die Gebühr ist also die Differenz zu Stück x Kurs
-- (fee_amount, berechnet von ComdirectFetch.Domain.TradeFeeCalculator). Dedup über die comdirect-
-- transactionId; bei erneutem Abruf werden die Werte aktualisiert (z. B. Buchungsstatus).
CREATE TABLE depot_transactions (
    id                        BIGINT AUTO_INCREMENT PRIMARY KEY,
    portfolio_id              BIGINT         NOT NULL,
    comdirect_transaction_id  VARCHAR(64)    NOT NULL,
    booking_status            VARCHAR(20)    NOT NULL,
    booking_date              DATE           NULL,
    business_date             DATE           NULL,
    transaction_type          VARCHAR(20)    NULL,
    direction                 VARCHAR(10)    NULL,
    isin                      VARCHAR(12)    NULL,
    wkn                       VARCHAR(6)     NULL,
    instrument_name           VARCHAR(255)   NULL,
    instrument_type           VARCHAR(40)    NULL,
    quantity                  DECIMAL(18,6)  NOT NULL,
    execution_price           DECIMAL(18,6)  NULL,
    transaction_value         DECIMAL(18,2)  NULL,
    currency                  CHAR(3)        NULL,
    fee_amount                DECIMAL(18,2)  NULL,
    first_seen_at             DATETIME(3)    NOT NULL,
    CONSTRAINT fk_depot_transactions_portfolio FOREIGN KEY (portfolio_id) REFERENCES portfolios (id),
    UNIQUE KEY uq_depot_transactions_comdirect_id (comdirect_transaction_id),
    KEY ix_depot_transactions_portfolio_business_date (portfolio_id, business_date)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

ALTER TABLE sync_log
    MODIFY COLUMN data_kind ENUM('TokenRefresh', 'Salden', 'Depotuebersicht', 'Kontoumsaetze', 'Konsolidierung', 'Depotumsaetze') NOT NULL;
