-- Schema-Version 0012: optionales Freitext-Kommentarfeld für Kategorisierungsregeln, damit
-- Nutzer festhalten können, warum eine Regel existiert (KONZEPT.md Abschnitt 12).
ALTER TABLE categorization_rules
    ADD COLUMN comment VARCHAR(500) NULL AFTER priority;
