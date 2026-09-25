# comdirect-fetch

Dienst, der in konfigurierbaren Intervallen Salden, Depotübersicht und Kontoumsätze aus
der comdirect REST API abruft und historisiert in einer extern bereitgestellten MariaDB
ablegt. Das vollständige Konzept (Architektur, Datenmodell, Auswertungen, offene Punkte)
steht in [`docs/konzept.md`](docs/konzept.md).

## Voraussetzungen

- .NET 10 SDK (für lokale Entwicklung) bzw. Docker
- Eine extern erreichbare MariaDB-Instanz (wird **nicht** von diesem Projekt bereitgestellt)
- comdirect-Zugangsdaten: Client-ID/Secret (API-Zugang) sowie Zugangsnummer/PIN

## Konfiguration

Alle Einstellungen werden über Umgebungsvariablen übergeben, siehe [`.env.example`](.env.example).
Für lokale Docker-Läufe: `.env.example` nach `.env` kopieren und Werte eintragen.

## Bauen und testen

```bash
dotnet build
dotnet test
```

## Starten

```bash
docker compose -f docker/docker-compose.yml up --build
```

Beim Start wendet der Dienst automatisch alle ausstehenden Datenbank-Migrationen aus
`db/migrations/` an.

## Erste Anmeldung (TAN-Freigabe) und Status

comdirect verlangt beim Aufbau einer neuen Session eine TAN-Bestätigung, die der Container
nicht automatisch erledigen kann (siehe `docs/konzept.md`, Abschnitt 3). Einfachster Weg über
[`scripts/comdirectctl.sh`](scripts/comdirectctl.sh) (Voraussetzung: `curl`, `jq`):

```bash
./scripts/comdirectctl.sh auth start
# → löst z. B. eine PushTAN-Benachrichtigung in der comdirect-App aus
# ACHTUNG: nicht wiederholt aufrufen, siehe Warnung unten zur TAN-Sperre

./scripts/comdirectctl.sh auth confirm
# tanCode als Argument nur nötig, wenn der TAN-Typ eine manuelle Eingabe verlangt
# (z. B. photoTAN/mobileTAN): ./scripts/comdirectctl.sh auth confirm <TAN_CODE>

./scripts/comdirectctl.sh status
# menschenlesbarer Status: Auth-Status, Zeilen je Tabelle, letzte Abrufe.
# Für Skripte/Monitoring: --json; Exit-Code 0 = authentifiziert, 1 = Freigabe nötig/Fehler,
# 2 = Dienst nicht erreichbar (siehe comdirectctl.sh help für alle Codes).
```

Äquivalent direkt per `curl` gegen die HTTP-API (Basis-URL per `COMDIRECT_FETCH_URL`
überschreibbar, Standard `http://localhost:8750`):

```bash
curl -X POST http://localhost:8750/auth/start
curl -X POST http://localhost:8750/auth/confirm -H "Content-Type: application/json" -d '{"tanCode": null}'
curl http://localhost:8750/health
```

(Port 8750 statt des ursprünglich geplanten 8080, da 8080 auf diesem Host bereits belegt war – siehe `docker/docker-compose.yml`.)

Solange der Dienst danach durchgehend läuft, hält ein interner Hintergrundprozess die
Session per Token-Refresh am Leben; eine erneute Freigabe ist erst nach einem Neustart
oder einer längeren Downtime wieder nötig.

**Test-/Betriebshilfen** (berühren keine Session/TAN, gefahrlos wiederholbar):
- `./scripts/comdirectctl.sh fetch-now` bzw. `POST /debug/fetch-now` – stößt Salden-, Depotübersicht- und Umsatzabruf sofort an, statt auf die konfigurierten Intervalle zu warten.
- `./scripts/comdirectctl.sh status` bzw. `GET /debug/summary` – Zeilenanzahl je Tabelle plus die letzten 10 `sync_log`-Einträge, zur schnellen Verifikation ohne direkten DB-Zugriff.

## ⚠️ TAN-Sperre – bitte unbedingt beachten

comdirect sperrt nach **drei falschen TAN-Eingaben** oder **fünf TAN-Challenges ohne
zwischenzeitliche Einlösung einer korrekten TAN** den **gesamten Online-Banking-Zugang**
(nicht nur den API-Zugriff). `POST /auth/start` daher nicht wiederholt/automatisiert
aufrufen – der Dienst liefert bei bereits ausstehender Freigabe die bestehende Challenge
zurück statt eine neue anzufordern, aber das schützt nicht vor externen Skripten/Retries.

## Stand der comdirect-Endpunkte

Die in `src/ComdirectFetch.Api` verwendeten Endpunkt-Pfade und JSON-Felder wurden gegen die
offizielle comdirect REST API Dokumentation abgeglichen **und zusätzlich mit echten
Zugangsdaten end-to-end live getestet**: Login/Session/TAN-Flow, Salden (3 Konten),
Depotübersicht (inkl. Positionen mit ISIN/Name) und Kontoumsätze (inkl. Pagination über
mehrere Seiten) funktionieren nachweislich (siehe `CHANGELOG.md` 0.2.0–0.7.0). Dabei wurden
mehrere reale Abweichungen von der Doku gefunden und behoben (u. a. `bookingDate` als
einfacher String statt verschachteltem Objekt, `paging-first` erfordert
`transactionState=BOOKED`, comdirects Rate-Limit bei vielen Anfragen).

Rate-Limiting (HTTP 429) wird seit 0.6.0 mit echtem Retry/Backoff behandelt (`ComdirectResilience`,
Polly), zusätzlich zu proaktiven kurzen Pausen zwischen Requests – siehe `CHANGELOG.md`.

**Bekannte Restrisiken**: Verhalten bei sehr großen Depots/vielen Konten im Dauerbetrieb ist
nur mit den aktuellen Testdaten verifiziert, nicht an echten Großvolumina. Fehlertexte mit
Umlauten wurden in den Logs teils falsch codiert dargestellt (rein kosmetisch, noch nicht
untersucht).

## Grafana-Dashboards

Dieses Projekt betreibt kein eigenes Grafana – die Auswertungen werden in eine bereits
vorhandene Grafana-Instanz eingebunden (KONZEPT.md Abschnitt 2/6: „Grafana ist optional"
heißt hier konkret: extern und schon da, nicht Teil dieses Deployments). Zwei Dashboards,
beide mit echten Daten verifiziert:

- **`grafana/dashboards/salden.json`** – „Salden & Vermögen" (Phase 1): Saldo-Verlauf je
  Konto plus Gesamtsumme, sowie Vermögensentwicklung (Konten + Depots kombiniert) mit
  Stat-Panels für die jeweils aktuellen Werte.
- **`grafana/dashboards/depot.json`** – „Depot" (Phase 2): Asset-Allokation als
  Kreisdiagramm, Positionstabelle, sowie Kurswert- und Gewinn/Verlust-Entwicklung je
  Einzelposition über die Zeit.

Beide JSON-Dateien sind die Quelle der Wahrheit und werden per
[Grafana-HTTP-API](https://grafana.com/docs/grafana/latest/developers/http_api/dashboard/)
in die vorhandene Instanz importiert (`POST /api/dashboards/db`, `overwrite: true` – dieselbe
Datei erneut posten überschreibt die vorhandene Version). Eine MySQL/MariaDB-Datenquelle mit
uid `comdirect-mariadb` muss dort einmalig angelegt sein (Host/Port/DB/User/Passwort aus der
`.env`); dafür sind in dieser bestehenden Grafana-Instanz Admin-Rechte nötig – ein
Service-Account mit nur Editor-Rolle darf keine Datenquellen anlegen.

## Versionierung

Siehe `docs/konzept.md` Abschnitt 8 und `CLAUDE.md`: Anwendungsversion (SemVer, zentral in
`ComdirectFetch.Domain.AppVersion`) und Datenbank-Schema-Version (`db/migrations/`) werden
bei jeder relevanten Änderung automatisch angepasst.
