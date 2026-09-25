-- Schema-Version 0005: korrigiert zwei Freitext-Muster aus 0004, die live nie getroffen
-- haben. Ursache (live beobachtet an echten Umsätzen): comdirect bricht den Buchungstext
-- alle 35 Zeichen um und fügt die zweistellige Zeilennummer MITTEN ins Wort ein, ohne
-- Leerzeichen. Dadurch steht der ursprüngliche Mehrwort-Ausdruck nie zusammenhängend im Text:
--   "Humble Bundle"              -> "Humble B02undle" (1. Vorkommen), "Humble03 Bundle" (2.)
--   "Headline - Noth, Schneider" -> "Headline02 - Noth, Schneider" (1.), "Headline - Noth, Sch04neider" (2.)
-- Kürzere Ein-Wort-Muster ("Humble", "Headline") bleiben in beiden Vorkommen unzerteilt und
-- sind daher robuster gegen diesen Zeilenumbruch. Append-only wie 0002-0004: 0004 wird nicht
-- editiert, stattdessen werden die betroffenen Regeln hier per UPDATE korrigiert.

UPDATE categorization_rules SET pattern = 'Humble' WHERE pattern = 'Humble Bundle' AND priority = 202;
UPDATE categorization_rules SET pattern = 'Headline' WHERE pattern = 'Headline - Noth, Schneider' AND priority = 260;
