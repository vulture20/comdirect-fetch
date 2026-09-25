-- Schema-Version 0006: persistenter, verschlüsselter Speicher für den comdirect-Session-
-- Token (KONZEPT.md Abschnitt 3, Issue "Auth-Status über Neustarts persistieren"). Erlaubt,
-- eine noch gültige Session nach einem Container-Neustart wiederherzustellen, ohne erneut
-- eine TAN-Freigabe zu benötigen - funktioniert nur, wenn der Neustart innerhalb der
-- Refresh-Token-Gültigkeit passiert (siehe Abschnitt 3); danach ist ohnehin eine neue
-- TAN-Freigabe nötig.
--
-- Verschlüsselt mit AES-256-GCM; der Schlüssel kommt ausschließlich aus der Umgebungsvariable
-- Comdirect__TokenEncryptionKeyBase64 und wird NIE in der Datenbank abgelegt. Ist die
-- Variable nicht gesetzt, bleibt diese Tabelle ungenutzt - Verhalten entspricht dann dem
-- Stand vor 0006 (In-Memory only, jeder Neustart braucht eine neue TAN-Freigabe).
--
-- Bewusst eine Einzelzeilen-Tabelle (id fest = 1): es gibt genau einen comdirect-Zugang
-- (KONZEPT.md Abschnitt 9, "Umfang").
CREATE TABLE auth_token_store (
    id          TINYINT UNSIGNED PRIMARY KEY,
    nonce       VARBINARY(16)   NOT NULL,
    ciphertext  VARBINARY(1024) NOT NULL,
    tag         VARBINARY(16)   NOT NULL,
    updated_at  DATETIME(3)     NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
