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

curl -X POST http://localhost:8080/auth/confirm \
  -H "Content-Type: application/json" \
  -d '{"tanCode": null}'
# tanCode nur nötig, wenn der TAN-Typ eine manuelle Eingabe verlangt (z. B. photoTAN/mobileTAN)
```

Solange der Dienst danach durchgehend läuft, hält ein interner Hintergrundprozess die
Session per Token-Refresh am Leben; eine erneute Freigabe ist erst nach einem Neustart
oder einer längeren Downtime wieder nötig.

`GET /health` zeigt den aktuellen Authentifizierungsstatus und die Anwendungsversion.

## Wichtiger Hinweis zu den comdirect-Endpunkten

Die in `src/ComdirectFetch.Api` verwendeten Endpunkt-Pfade und JSON-Felder sind aus
comdirect-Community-Quellen und quelloffenen Clients rekonstruiert, **nicht** gegen die
offizielle Swagger/Postman-Collection (developer.comdirect.de) verifiziert. Vor dem ersten
produktiven Lauf mit echten Zugangsdaten abgleichen und bei Abweichungen die DTOs in
`BankingModels.cs`/`BrokerageModels.cs`/`SessionModels.cs` sowie die Pfade in den
jeweiligen Clients anpassen.

## Versionierung

Siehe `docs/konzept.md` Abschnitt 8 und `CLAUDE.md`: Anwendungsversion (SemVer, zentral in
`ComdirectFetch.Domain.AppVersion`) und Datenbank-Schema-Version (`db/migrations/`) werden
bei jeder relevanten Änderung automatisch angepasst.
