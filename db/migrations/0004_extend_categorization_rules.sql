-- Schema-Version 0004: Erweiterung der Kategorisierungsregeln anhand echter Umsatzdaten
-- (KONZEPT.md Abschnitt 6/9). Neue, spezifischere Kategorien für die geplante Phase-3-
-- Cashflow-Analyse; behebt außerdem die Fehlkategorisierung von Zinsgutschriften (bisher
-- zeigte keine Regel auf "Zinsen/Dividenden"). Append-only wie 0002/0003 - diese Datei wird
-- nach dem Release nie mehr geändert, nur durch weitere Migrationen ergänzt.
--
-- Bewusst NICHT behandelt: die Buchung "Kd.1209379067 Wir sagen Danke. RG-Nr..." - generische
-- Rechnungs-/Mahnungsformulierung, Händler aus dem Text nicht zuverlässig erkennbar; bleibt
-- im Vorzeichen-Fallback ("Sonstige Ausgabe"), statt zu raten.
--
-- PayPal-geroutete Buchungen (Humble Bundle, CinemaxX, Headline) matchen bewusst den
-- Händlernamen, NICHT das gemeinsame PayPal-Routing-Präfix (z. B. "PP.7563.PP") - das ist
-- PayPals eigene ID, nicht händlerspezifisch, und würde künftige fremde PayPal-Zahlungen
-- fälschlich in dieselbe Kategorie stecken. Gleiches Prinzip bei "SumUp" (generischer
-- Terminal-Anbieter vieler Händler): Muster ist der konkrete Café-Name "miamamia", nicht
-- "SumUp".

INSERT INTO categories (name, type) VALUES
    ('Gaming/Unterhaltung', 'Ausgabe'),
    ('Restaurants/Cafés', 'Ausgabe'),
    ('Telekommunikation', 'Ausgabe'),
    ('Altersvorsorge', 'Ausgabe'),
    ('Öffentlicher Nahverkehr', 'Ausgabe'),
    ('Apotheke/Gesundheit', 'Ausgabe'),
    ('Mode/Bekleidung', 'Ausgabe');

-- Priorität: 140er/160er-Block erweitert bestehende Kategorien (Lücke für weitere Muster
-- offen gehalten); 170 neu für Zinsen/Dividenden; ab 200 je ein 10er-Block pro neuer
-- Kategorie (Platz für spätere Ergänzungen ohne Umnummerierung).
INSERT INTO categorization_rules (pattern, match_field, category_id, priority)
SELECT 'ALDI SUED', 'BookingText', id, 142 FROM categories WHERE name = 'Lebensmittel/Einzelhandel'
UNION ALL
SELECT 'Visa-Kreditkarte', 'BookingText', id, 161 FROM categories WHERE name = 'Kontoführungsgebühren'
UNION ALL
SELECT 'Abschluss Zinsen', 'BookingText', id, 170 FROM categories WHERE name = 'Zinsen/Dividenden'
UNION ALL
SELECT 'STEAMGAMES.COM', 'BookingText', id, 200 FROM categories WHERE name = 'Gaming/Unterhaltung'
UNION ALL
SELECT 'STEAM PURCHASE', 'BookingText', id, 201 FROM categories WHERE name = 'Gaming/Unterhaltung'
UNION ALL
SELECT 'Humble Bundle', 'BookingText', id, 202 FROM categories WHERE name = 'Gaming/Unterhaltung'
UNION ALL
SELECT 'CinemaxX', 'BookingText', id, 203 FROM categories WHERE name = 'Gaming/Unterhaltung'
UNION ALL
SELECT 'KFC MUELHEIM', 'BookingText', id, 210 FROM categories WHERE name = 'Restaurants/Cafés'
UNION ALL
SELECT 'miamamia', 'BookingText', id, 211 FROM categories WHERE name = 'Restaurants/Cafés'
UNION ALL
SELECT 'congstar', 'BookingText', id, 220 FROM categories WHERE name = 'Telekommunikation'
UNION ALL
SELECT 'FirmenRente', 'BookingText', id, 230 FROM categories WHERE name = 'Altersvorsorge'
UNION ALL
SELECT 'Bochum-Gelsenkirchener', 'BookingText', id, 240 FROM categories WHERE name = 'Öffentlicher Nahverkehr'
UNION ALL
SELECT 'Storchen Apotheke', 'BookingText', id, 250 FROM categories WHERE name = 'Apotheke/Gesundheit'
UNION ALL
SELECT 'Headline - Noth, Schneider', 'BookingText', id, 260 FROM categories WHERE name = 'Mode/Bekleidung';
