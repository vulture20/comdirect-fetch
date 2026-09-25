-- Schema-Version 0007: verschlüsselter Speicher für die comdirect-Zugangsnummer/PIN
-- (docs/konzept.md Abschnitt 10, "Sichere Ablage der comdirect-Zugangsdaten"). Bewusst
-- getrennt von auth_token_store (0006, Session-Token) und mit einem eigenen, dedizierten
-- Schlüssel verschlüsselt (Comdirect__CredentialKeyFilePath), nicht mit
-- Comdirect__TokenEncryptionKeyBase64 – unterschiedliche Geheimnisse, unterschiedliche
-- Schlüssel. Client-ID/Client-Secret werden bewusst NICHT hier abgelegt, sondern über
-- Docker-Compose-Secrets bereitgestellt (siehe Konzept Abschnitt 10 A).
--
-- Wird ausschließlich über den Bootstrap-Endpunkt POST /admin/credentials befüllt; ist
-- kein Schlüssel unter dem konfigurierten Pfad vorhanden, bleibt diese Tabelle ungenutzt
-- und Zugangsnummer/PIN werden weiterhin aus der Konfiguration (.env) gelesen.
CREATE TABLE credential_store (
    id          TINYINT UNSIGNED PRIMARY KEY,
    nonce       VARBINARY(16)   NOT NULL,
    ciphertext  VARBINARY(1024) NOT NULL,
    tag         VARBINARY(16)   NOT NULL,
    updated_at  DATETIME(3)     NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
