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

## Erste Anmeldung (TAN-Freigabe)

comdirect verlangt beim Aufbau einer neuen Session eine TAN-Bestätigung, die der Container
nicht automatisch erledigen kann (siehe `docs/konzept.md`, Abschnitt 3). Nach dem Start:

```bash
curl -X POST http://localhost:8080/auth/start
# → löst z. B. eine PushTAN-Benachrichtigung in der comdirect-App aus
# ACHTUNG: nicht wiederholt aufrufen, siehe Warnung oben zur TAN-Sperre

curl -X POST http://localhost:8080/auth/confirm \
  -H "Content-Type: application/json" \
  -d '{"tanCode": null}'
# tanCode nur nötig, wenn der TAN-Typ eine manuelle Eingabe verlangt (z. B. photoTAN/mobileTAN)
```

Solange der Dienst danach durchgehend läuft, hält ein interner Hintergrundprozess die
Session per Token-Refresh am Leben; eine erneute Freigabe ist erst nach einem Neustart
oder einer längeren Downtime wieder nötig.

`GET /health` zeigt den aktuellen Authentifizierungsstatus und die Anwendungsversion.

**Test-/Betriebshilfen** (berühren keine Session/TAN, gefahrlos wiederholbar):
- `POST /debug/fetch-now` – stößt Salden-, Depotübersicht- und Umsatzabruf sofort an, statt auf die konfigurierten Intervalle zu warten.
- `GET /debug/summary` – Zeilenanzahl je Tabelle plus die letzten 10 `sync_log`-Einträge, zur schnellen Verifikation ohne direkten DB-Zugriff.

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
mehrere Seiten) funktionieren nachweislich (siehe `CHANGELOG.md` 0.2.0–0.5.0). Dabei wurden
mehrere reale Abweichungen von der Doku gefunden und behoben (u. a. `bookingDate` als
einfacher String statt verschachteltem Objekt, `paging-first` erfordert
`transactionState=BOOKED`, comdirects Rate-Limit bei vielen Anfragen).

**Bekannte Restrisiken**: Rate-Limiting bei sehr großen Depots/vielen Konten im Dauerbetrieb
ist nur ansatzweise (Pausen zwischen Requests) adressiert, nicht mit Retry/Backoff. Fehler-
texte mit Umlauten wurden in den Logs teils falsch codiert dargestellt (rein kosmetisch,
noch nicht untersucht).

## Versionierung

Siehe `docs/konzept.md` Abschnitt 8 und `CLAUDE.md`: Anwendungsversion (SemVer, zentral in
`ComdirectFetch.Domain.AppVersion`) und Datenbank-Schema-Version (`db/migrations/`) werden
bei jeder relevanten Änderung automatisch angepasst.
