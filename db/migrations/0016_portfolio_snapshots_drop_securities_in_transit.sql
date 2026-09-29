-- Schema-Version 0016: securities_in_transit (0015) wieder entfernt. Die Annahme dahinter - gekaufte
-- Wertpapiere erscheinen erst zur Valuta im Depot - war falsch: comdirect zeigt eine ausgeführte Order
-- sofort in den Positionen (Munich Re: 28.09. morgens, Valuta 30.09.). Das Guthaben je Snapshot wird
-- stattdessen aus dem aktuellen Saldo und den Buchungen zurückgerechnet (DepotPerformanceCalculator),
-- eine Korrekturspalte für "unterwegs befindliche" Käufe ist damit überflüssig (KONZEPT.md Abschnitt 13).
ALTER TABLE portfolio_snapshots DROP COLUMN securities_in_transit;
