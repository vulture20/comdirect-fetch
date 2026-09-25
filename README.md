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

## ⚠️ TAN-Sperre – bitte unbedingt beachten

comdirect sperrt nach **drei falschen TAN-Eingaben** oder **fünf TAN-Challenges ohne
zwischenzeitliche Einlösung einer korrekten TAN** den **gesamten Online-Banking-Zugang**
(nicht nur den API-Zugriff). `POST /auth/start` daher nicht wiederholt/automatisiert
aufrufen – der Dienst liefert bei bereits ausstehender Freigabe die bestehende Challenge
zurück statt eine neue anzufordern, aber das schützt nicht vor externen Skripten/Retries.

## Stand der comdirect-Endpunkte

Die in `src/ComdirectFetch.Api` verwendeten Endpunkt-Pfade und JSON-Felder wurden gegen die
offizielle comdirect REST API Dokumentation (Swagger, Postman-Collection, PDF-Spezifikation)
abgeglichen und korrigiert – siehe `CHANGELOG.md` 0.2.0. Strukturell verifiziert; ein
Testlauf gegen die echte API mit echten Zugangsdaten steht aber noch aus. Bei
Abweichungen die DTOs in `BankingModels.cs`/`BrokerageModels.cs`/`SessionModels.cs` sowie
die Pfade in den jeweiligen Clients anpassen.

## Versionierung

Siehe `docs/konzept.md` Abschnitt 8 und `CLAUDE.md`: Anwendungsversion (SemVer, zentral in
`ComdirectFetch.Domain.AppVersion`) und Datenbank-Schema-Version (`db/migrations/`) werden
bei jeder relevanten Änderung automatisch angepasst.
