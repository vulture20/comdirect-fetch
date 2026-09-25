-- Schema-Version 0001: initiales Schema (KONZEPT.md Abschnitt 5)
-- Migrationen sind append-only: dieses Skript wird nach dem ersten Release nie mehr
-- geändert, nur durch neue, fortlaufend nummerierte Dateien ergänzt (KONZEPT.md Abschnitt 8).

CREATE TABLE accounts (
    id                    BIGINT AUTO_INCREMENT PRIMARY KEY,
    comdirect_account_id  VARCHAR(64)  NOT NULL,
    iban                  VARCHAR(34)  NULL,
    account_type          VARCHAR(64)  NOT NULL,
    display_name          VARCHAR(255) NOT NULL,
    currency              CHAR(3)      NOT NULL,
    UNIQUE KEY uq_accounts_comdirect_account_id (comdirect_account_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE account_balances (
    id                BIGINT AUTO_INCREMENT PRIMARY KEY,
    account_id        BIGINT         NOT NULL,
    recorded_at       DATETIME(3)    NOT NULL,
    balance           DECIMAL(18,2)  NOT NULL,
    available_amount  DECIMAL(18,2)  NOT NULL,
    currency          CHAR(3)        NOT NULL,
    CONSTRAINT fk_account_balances_account FOREIGN KEY (account_id) REFERENCES accounts (id),
    KEY ix_account_balances_account_recorded (account_id, recorded_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE portfolios (
    id                      BIGINT AUTO_INCREMENT PRIMARY KEY,
    comdirect_portfolio_id  VARCHAR(64)  NOT NULL,
    display_name            VARCHAR(255) NOT NULL,
    UNIQUE KEY uq_portfolios_comdirect_portfolio_id (comdirect_portfolio_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE portfolio_snapshots (
    id                 BIGINT AUTO_INCREMENT PRIMARY KEY,
    portfolio_id       BIGINT         NOT NULL,
    recorded_at        DATETIME(3)    NOT NULL,
    total_value        DECIMAL(18,2)  NOT NULL,
    acquisition_value  DECIMAL(18,2)  NOT NULL,
    currency           CHAR(3)        NOT NULL,
    CONSTRAINT fk_portfolio_snapshots_portfolio FOREIGN KEY (portfolio_id) REFERENCES portfolios (id),
    KEY ix_portfolio_snapshots_portfolio_recorded (portfolio_id, recorded_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE portfolio_positions (
    id                 BIGINT AUTO_INCREMENT PRIMARY KEY,
    snapshot_id        BIGINT         NOT NULL,
    isin               VARCHAR(12)    NULL,
    wkn                VARCHAR(6)     NULL,
    display_name       VARCHAR(255)   NOT NULL,
    quantity           DECIMAL(18,6)  NOT NULL,
    market_value       DECIMAL(18,2)  NOT NULL,
    acquisition_value  DECIMAL(18,2)  NOT NULL,
    profit_loss        DECIMAL(18,2)  NOT NULL,
    currency           CHAR(3)        NOT NULL,
    CONSTRAINT fk_portfolio_positions_snapshot FOREIGN KEY (snapshot_id) REFERENCES portfolio_snapshots (id),
    KEY ix_portfolio_positions_snapshot (snapshot_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE categories (
    id    BIGINT AUTO_INCREMENT PRIMARY KEY,
    name  VARCHAR(100) NOT NULL,
    type  ENUM('Einnahme', 'Ausgabe', 'InternNeutral') NOT NULL,
    UNIQUE KEY uq_categories_name (name)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE categorization_rules (
    id           BIGINT AUTO_INCREMENT PRIMARY KEY,
    pattern      VARCHAR(255) NOT NULL,
    match_field  ENUM('BookingText', 'TransactionType') NOT NULL,
    category_id  BIGINT NOT NULL,
    priority     INT NOT NULL,
    CONSTRAINT fk_categorization_rules_category FOREIGN KEY (category_id) REFERENCES categories (id),
    KEY ix_categorization_rules_priority (priority)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

-- Dedup-Schlüssel (account_id, comdirect_reference): siehe offener Punkt in KONZEPT.md
-- Abschnitt 9 zur Eindeutigkeit der comdirect-Umsatz-Referenz. Falls sich das nicht als
-- stabil erweist, muss dieser Schlüssel in einer Folge-Migration angepasst werden.
CREATE TABLE transactions (
    id                     BIGINT AUTO_INCREMENT PRIMARY KEY,
    account_id             BIGINT         NOT NULL,
    comdirect_reference    VARCHAR(128)   NOT NULL,
    booking_date           DATE           NOT NULL,
    value_date             DATE           NULL,
    amount                 DECIMAL(18,2)  NOT NULL,
    currency               CHAR(3)        NOT NULL,
    booking_text           VARCHAR(512)   NULL,
    transaction_type       VARCHAR(64)    NULL,
    category_id            BIGINT         NULL,
    manually_categorized   TINYINT(1)     NOT NULL DEFAULT 0,
    first_seen_at          DATETIME(3)    NOT NULL,
    CONSTRAINT fk_transactions_account FOREIGN KEY (account_id) REFERENCES accounts (id),
    CONSTRAINT fk_transactions_category FOREIGN KEY (category_id) REFERENCES categories (id),
    UNIQUE KEY uq_transactions_account_reference (account_id, comdirect_reference),
    KEY ix_transactions_account_booking_date (account_id, booking_date)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE sync_log (
    id                    BIGINT AUTO_INCREMENT PRIMARY KEY,
    data_kind             ENUM('TokenRefresh', 'Salden', 'Depotuebersicht', 'Kontoumsaetze') NOT NULL,
    account_id            BIGINT       NULL,
    portfolio_id          BIGINT       NULL,
    application_version   VARCHAR(32)  NOT NULL,
    started_at            DATETIME(3)  NOT NULL,
    finished_at           DATETIME(3)  NULL,
    status                ENUM('Erfolgreich', 'Fehlgeschlagen', 'FreigabeErforderlich') NOT NULL,
    error_message         TEXT         NULL,
    CONSTRAINT fk_sync_log_account FOREIGN KEY (account_id) REFERENCES accounts (id),
    CONSTRAINT fk_sync_log_portfolio FOREIGN KEY (portfolio_id) REFERENCES portfolios (id),
    KEY ix_sync_log_started_at (started_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
