-- Schema-Version 0002: Basis-Kategorien und Start-Regelset für die Kategorisierungslogik
-- (KONZEPT.md Abschnitt 6). Reines Startset für den MVP, wird iterativ mit echten
-- Umsatzdaten verfeinert - siehe KONZEPT.md Abschnitt 9, offener Punkt "Kategorisierung
-- von Buchungstexten".

INSERT INTO categories (name, type) VALUES
    ('Intern/Neutral', 'InternNeutral'),
    ('Gehalt/Lohn', 'Einnahme'),
    ('Zinsen/Dividenden', 'Einnahme'),
    ('Sonstige Einnahme', 'Einnahme'),
    ('Miete/Wohnen', 'Ausgabe'),
    ('Versicherungen', 'Ausgabe'),
    ('Abonnements', 'Ausgabe'),
    ('Lebensmittel/Einzelhandel', 'Ausgabe'),
    ('Versandhandel', 'Ausgabe'),
    ('Ordergebühren', 'Ausgabe'),
    ('Kontoführungsgebühren', 'Ausgabe'),
    ('Sonstige Ausgabe', 'Ausgabe');

-- Strukturiertes Feld (transaction_type) zuerst, danach Freitext-Muster (booking_text).
-- Priorität aufsteigend: niedrigere Zahl wird zuerst geprüft, erste Übereinstimmung gewinnt.
INSERT INTO categorization_rules (pattern, match_field, category_id, priority)
SELECT 'Wertpapierabrechnung', 'TransactionType', id, 10 FROM categories WHERE name = 'Ordergebühren'
UNION ALL
SELECT 'Dauerauftrag', 'TransactionType', id, 20 FROM categories WHERE name = 'Miete/Wohnen'
UNION ALL
SELECT 'GEHALT', 'BookingText', id, 100 FROM categories WHERE name = 'Gehalt/Lohn'
UNION ALL
SELECT 'LOHN', 'BookingText', id, 101 FROM categories WHERE name = 'Gehalt/Lohn'
UNION ALL
SELECT 'MIETE', 'BookingText', id, 110 FROM categories WHERE name = 'Miete/Wohnen'
UNION ALL
SELECT 'VERSICHERUNG', 'BookingText', id, 120 FROM categories WHERE name = 'Versicherungen'
UNION ALL
SELECT 'NETFLIX', 'BookingText', id, 130 FROM categories WHERE name = 'Abonnements'
UNION ALL
SELECT 'SPOTIFY', 'BookingText', id, 131 FROM categories WHERE name = 'Abonnements'
UNION ALL
SELECT 'REWE', 'BookingText', id, 140 FROM categories WHERE name = 'Lebensmittel/Einzelhandel'
UNION ALL
SELECT 'EDEKA', 'BookingText', id, 141 FROM categories WHERE name = 'Lebensmittel/Einzelhandel'
UNION ALL
SELECT 'AMAZON', 'BookingText', id, 150 FROM categories WHERE name = 'Versandhandel'
UNION ALL
SELECT 'KONTOFUEHRUNGSGEBUEHR', 'BookingText', id, 160 FROM categories WHERE name = 'Kontoführungsgebühren';
