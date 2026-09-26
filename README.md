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

## Sichere Ablage der comdirect-Zugangsdaten

Seit 0.12.0 (`docs/konzept.md` Abschnitt 10) werden Client-ID/Client-Secret und Zugangsnummer/PIN
nicht mehr nur als Klartext in `.env` gehalten – siehe dort für das vollständige Konzept
(Bedrohungsmodell, Begründung). Einmaliges Setup vor dem ersten `docker compose up`:

```bash
# 1. Client-ID/Client-Secret als Docker-Compose-Secret-Dateien anlegen (ersetzt die
#    entsprechenden Zeilen in .env; secrets/ ist gitignored):
mkdir -p secrets
printf '%s' 'DEINE_CLIENT_ID'     > secrets/Comdirect__ClientId
printf '%s' 'DEIN_CLIENT_SECRET'  > secrets/Comdirect__ClientSecret
chmod 600 secrets/Comdirect__ClientId secrets/Comdirect__ClientSecret

# 2. Dedizierten Schlüssel für die Zugangsnummer/PIN-Verschlüsselung erzeugen - bewusst
#    außerhalb dieses Projektverzeichnisses, damit er nicht dieselbe Exposition wie .env hat:
./scripts/comdirectctl.sh generate-bootstrap-key
# Standardpfad /etc/comdirect-fetch/credential.key (überschreibbar per Argument), chmod 400.
# Bricht bewusst ab, falls dort schon ein Schlüssel liegt.
```

Auch der Schlüssel für die Session-Token-Persistierung (`Comdirect__TokenEncryptionKeyBase64`,
siehe unten) lässt sich so erzeugen: `./scripts/comdirectctl.sh generate-token-key` gibt einen
zufälligen Base64-Schlüssel aus, der manuell in `.env` einzutragen ist.

Danach Container (neu) starten und Zugangsnummer/PIN einmalig per Bootstrap-Schritt verschlüsselt
ablegen:

```bash
./scripts/comdirectctl.sh set-credentials
# fragt Zugangsnummer und PIN interaktiv ab (PIN nicht sichtbar, landet nicht in der
# Shell-History) und legt sie verschlüsselt in credential_store ab
```

Nach erfolgreicher Bestätigung `Comdirect__Username`/`Comdirect__Password` aus `.env` entfernen
– der Dienst liest sie danach ausschließlich verschlüsselt aus der DB. Ohne diesen Bootstrap-
Schritt (bzw. ohne die Schlüsseldatei) bleibt das bisherige Verhalten unverändert: Zugangsnummer/
PIN werden dann weiterhin aus `.env` gelesen, komplett opt-in.

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

## CI

[`.github/workflows/ci.yml`](.github/workflows/ci.yml) baut und testet automatisch bei jedem
Push auf einen Branch sowie bei jedem Pull Request gegen `main` – unabhängig vom Release-Workflow
unten, der nur bei Versions-Tags läuft. Kein Docker-Image, keine Veröffentlichung, reines
Build+Test-Gate.

## Fertige Images

Bei jedem gepushten Versions-Tag (`vX.Y.Z`) baut und veröffentlicht
[`.github/workflows/docker-release.yml`](.github/workflows/docker-release.yml) automatisch
ein Docker-Image nach GitHub Container Registry, getaggt sowohl mit der Versionsnummer als
auch mit `latest`:

```bash
docker pull ghcr.io/vulture20/comdirect-fetch:latest
docker pull ghcr.io/vulture20/comdirect-fetch:v0.7.0
```

Vorher läuft `dotnet build`/`dotnet test` als Gate – schlägt das fehl, wird nichts
veröffentlicht. Manueller Testlauf (ohne `latest` zu überschreiben) über
„Run workflow" im Actions-Tab bzw. `gh workflow run docker-release.yml`.

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
- `./scripts/comdirectctl.sh consolidate` bzw. `POST /debug/consolidate` – stößt den Konsolidierungs-/Aufräumlauf sofort an (KONZEPT.md Abschnitt 11). Komplett opt-in: ohne gesetzte `Retention__*`-Zeiträume (siehe `.env.example`) ein no-op.
- `./scripts/comdirectctl.sh notify-test` bzw. `POST /debug/notify-test` – löst eine Testbenachrichtigung über alle aktivierten Kanäle aus (siehe unten), ohne eine echte Session-Störung abwarten zu müssen.

## Benachrichtigung bei erforderlicher TAN-Freigabe

Bricht die Session-Refresh-Kette ab (Neustart, Downtime), wechselt der Status auf "Freigabe
erforderlich" – standardmäßig nur passiv über `comdirectctl.sh status`/`sync_log` sichtbar.
Optional, komplett opt-in und gleichzeitig nutzbar, zwei aktive Benachrichtigungskanäle (siehe
`.env.example` für alle Variablen):

- **E-Mail** – `Notification__EmailSmtpHost`, `-Port`, `-User`, `-Password`, `-UseStartTls`,
  `-From`, `-To` (kommagetrennt bei mehreren Empfängern).
- **Webhook** – `Notification__WebhookUrl`, POST mit generischem JSON-Body
  (`{"event", "subject", "body", "occurredAt", "appVersion"}`), funktioniert z. B. mit
  ntfy.sh, Home Assistant oder n8n/Node-RED.

Beide Kanäle lassen sich gleichzeitig konfigurieren; ist keiner gesetzt, ändert sich nichts am
bisherigen, rein passiven Verhalten. Konfiguration mit `./scripts/comdirectctl.sh notify-test`
prüfen, bevor man sich darauf verlässt.

## Kategorien &amp; Regeln bearbeiten

Kleine, vom Dienst selbst ausgelieferte Web-Oberfläche unter `/admin/rules/` (z. B.
`http://localhost:8750/admin/rules/`) – Tabellen-Editor für `categories`/`categorization_rules`,
inkl. Testen gegen echte Umsätze vor dem Übernehmen (Einzel-Regel-Vorschau und volle Simulation
mit Diff). Regeln können gegen Buchungstext, Umsatztyp oder – seit 0.17.0 – gegen den
strukturierten Empfänger-/Auftraggeber-Namen (`CounterpartyName`) geprüft werden, wichtig für
echte Überweisungen, deren Buchungstext nur den Verwendungszweck enthält. Ein Button „Nicht
kategorisiert" (seit 0.18.0) listet alle Umsätze ohne echte Kategorisierung (keine Kategorie
oder nur der Vorzeichen-Fallback) – gute Kandidaten für neue Regeln, ohne manuell in der DB
nachsehen zu müssen. Setzt `Admin__Password`
in `.env` voraus (HTTP Basic Auth, Benutzername beliebig) –
ohne gesetztes Passwort liefert `/admin/rules/*` durchgängig HTTP 503 statt ungeschützt erreichbar
zu sein. Bewusst strenger geschützt als die übrigen `/debug/*`/`/auth/*`-Endpunkte, da hier
dauerhafte Konfiguration geändert wird statt nur eine Aktion angestoßen. Die drei besonderen
Kategorien (`Intern/Neutral`, `Sonstige Einnahme`, `Sonstige Ausgabe`) lassen sich weder löschen
noch umbenennen.

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
nur mit den aktuellen Testdaten verifiziert, nicht an echten Großvolumina. Die früher teils
falsch codiert dargestellten Umlaute in geloggten Fehlertexten (Issue #8) sind behoben – siehe
`CHANGELOG.md`.

## Grafana-Dashboards

Dieses Projekt betreibt kein eigenes Grafana – die Auswertungen werden in eine bereits
vorhandene Grafana-Instanz eingebunden (KONZEPT.md Abschnitt 2/6: „Grafana ist optional"
heißt hier konkret: extern und schon da, nicht Teil dieses Deployments). Vier Dashboards,
alle mit echten Daten verifiziert:

- **`grafana/dashboards/salden.json`** – „Salden & Vermögen" (Phase 1): Saldo-Verlauf je
  Konto plus Gesamtsumme, sowie Vermögensentwicklung (Konten + Depots kombiniert) mit
  Stat-Panels für die jeweils aktuellen Werte.
- **`grafana/dashboards/depot.json`** – „Depot" (Phase 2): Asset-Allokation als
  Kreisdiagramm sowohl je Einzelposition als auch nach Anlageklasse gruppiert (Aktie/ETF/
  Fonds/Zertifikat/… – Issue #4), Positionstabelle, sowie Kurswert- und
  Gewinn/Verlust-Entwicklung je Einzelposition über die Zeit.
- **`grafana/dashboards/cashflow.json`** – „Cashflow & Kosten" (Phase 3): Einnahmen/
  Ausgaben/Netto je Monat, Ausgaben nach Kategorie, Gebührenübersicht (Kontoführungs-/
  Ordergebühren). Interne Umbuchungen zwischen eigenen Konten sind ausgeschlossen. Enthält
  seit 0.18.0 einen Dashboard-Link „Kategorien/Regeln bearbeiten →", der `/admin/rules/`
  direkt öffnet.
- **`grafana/dashboards/depot-performance.json`** – „Depot-Performance" (Phase 4,
  vereinfacht): Depotwert vs. Kapitaleinsatz, unrealisierter Gewinn/Verlust absolut und in
  Prozent. Enthält bewusst **keine** realisierten Gewinne aus verkauften Positionen und
  **keine** externen Ein-/Auszahlungen – dafür fehlt aktuell die Datengrundlage (siehe
  `docs/konzept.md` Abschnitt 9).

Alle JSON-Dateien sind die Quelle der Wahrheit und werden per
[Grafana-HTTP-API](https://grafana.com/docs/grafana/latest/developers/http_api/dashboard/)
in die vorhandene Instanz importiert (`POST /api/dashboards/db`, `overwrite: true` – dieselbe
Datei erneut posten überschreibt die vorhandene Version). Eine MySQL/MariaDB-Datenquelle mit
uid `comdirect-mariadb` muss dort angelegt sein (Host/Port/DB/User/Passwort aus der `.env`);
dafür sind in dieser bestehenden Grafana-Instanz Admin-Rechte nötig – ein Service-Account mit
nur Editor-Rolle darf keine Datenquellen anlegen.

Beides (Datenquelle + alle vier Dashboards) automatisiert per
[`scripts/grafana-setup.sh`](scripts/grafana-setup.sh) statt manueller Ad-hoc-API-Calls –
nützlich bei Erstinbetriebnahme oder nach einem Grafana-Rebuild auf einem neuen Host:

```bash
GRAFANA_TOKEN=<Service-Account-Token mit Admin-Rolle> ./scripts/grafana-setup.sh
# GRAFANA_URL überschreibbar (Standard: http://localhost:3000)
# Nur die Datenquelle: ./scripts/grafana-setup.sh datasource
# Nur die Dashboards:  ./scripts/grafana-setup.sh dashboards
```

Idempotent – ein erneuter Lauf aktualisiert eine bereits vorhandene Datenquelle/Dashboards
(gleiche `uid`), statt sie zu duplizieren. Datenbank-Verbindungsdaten für die Datenquelle liest
das Skript aus der lokalen `.env`.

## Versionierung

Siehe `docs/konzept.md` Abschnitt 8 und `CLAUDE.md`: Anwendungsversion (SemVer, zentral in
`ComdirectFetch.Domain.AppVersion`) und Datenbank-Schema-Version (`db/migrations/`) werden
bei jeder relevanten Änderung automatisch angepasst.
