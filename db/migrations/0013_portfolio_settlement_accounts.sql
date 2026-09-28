-- Schema-Version 0013: Depot<->Verrechnungskonto-Verknüpfung (Issue #12, KONZEPT.md Abschnitt 6
-- Phase 4) - comdirect liefert defaultSettlementAccountId/settlementAccountIds je Depot, bisher
-- nicht gespeichert. n:m, da ein Depot laut comdirect-Schema mehrere Verrechnungskonten haben
-- kann (is_default markiert defaultSettlementAccountId).
CREATE TABLE portfolio_settlement_accounts (
    portfolio_id BIGINT     NOT NULL,
    account_id   BIGINT     NOT NULL,
    is_default   TINYINT(1) NOT NULL DEFAULT 0,
    PRIMARY KEY (portfolio_id, account_id),
    CONSTRAINT fk_portfolio_settlement_accounts_portfolio FOREIGN KEY (portfolio_id) REFERENCES portfolios (id),
    CONSTRAINT fk_portfolio_settlement_accounts_account FOREIGN KEY (account_id) REFERENCES accounts (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
