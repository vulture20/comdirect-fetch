# Changelog

Versionshistorie der Anwendung (Semantic Versioning, siehe `docs/konzept.md` Abschnitt 8).
Die Datenbank-Schema-Version wird separat über die fortlaufend nummerierten Dateien in
`db/migrations/` nachvollzogen.

## 0.7.0 – Phase 1/2 der Auswertungen abgeschlossen, Bedien-Skript

- **Vermögensentwicklung** (Phase 1 vervollständigt): neues Panel im Salden-Dashboard,
  das Kontosalden je Salden-Abruf mit dem jeweils zuletzt bekannten Gesamt-Depotwert
  summiert (Salden und Depotübersicht laufen auf unterschiedlichen Intervallen, daher
  korrelierte Subquery statt exaktem Zeitstempel-Join). Dazu ein Stat-Panel
  „Vermögen aktuell". Dashboard entsprechend umbenannt zu „Salden & Vermögen".
- **Neues Depot-Dashboard** (`grafana/dashboards/depot.json`, Phase 2): Asset-Allokation
  als Kreisdiagramm (aktuellste Depotübersicht), Positionstabelle, sowie zwei
  Zeitreihen-Panels für Kurswert- und Gewinn/Verlust-Entwicklung je Einzelposition.
  Alle Panels mit echten Live-Testdaten verifiziert (Vermögen ~52.100 €, 21 Positionen
  über 4 Snapshots).
- **`scripts/comdirectctl.sh`**: Bash-Hilfsskript für TAN-Freigabe (`auth start`/
  `auth confirm`) und Status-Abfrage (`status`, `status --json`). Status ist sowohl
  menschenlesbar formatiert als auch als rohes JSON für Monitoring/Automatisierung
  verfügbar, mit sprechenden Exit-Codes (0 = authentifiziert, 1 = keine/ausstehende
  Freigabe oder Fehlerantwort, 2 = Dienst nicht erreichbar, 3 = fehlende Abhängigkeiten
  curl/jq, 64 = falscher Aufruf). Enthält denselben TAN-Sperre-Warnhinweis wie
  README.md/CLAUDE.md. Beim Testen einen Bash-Fallstrick gefunden: unter `set -e`
  überschrieb ein zuletzt "fehlgeschlagen" ausgewerteter `[[ ]] && ...`-Befehl im
  `EXIT`-Trap den eigentlichen `exit`-Code des Skripts – behoben durch `if`-Statement
  statt `&&`-Verkettung in der Cleanup-Funktion.

## 0.6.0 – Echtes Rate-Limit-Handling und Grafana-Dashboard

- Die festen 300ms-Pausen aus 0.5.0 durch eine richtige Retry-Lösung ergänzt:
  `ComdirectResilience` (Polly) wiederholt bei HTTP 429/5xx mit exponentiellem Backoff und
  Jitter, respektiert einen `Retry-After`-Header falls vorhanden. Bewusst nur für Token- und
  Datenendpunkte (Salden/Depot/Umsätze) aktiv – die TAN-Endpunkte (Session validieren/
  aktivieren) bleiben ohne automatischen Retry, um nicht ungewollt Richtung 5-Challenge-
  Sperre zu zählen (siehe `ComdirectAuthClient`).
- Grafana-Dashboard „Salden" hinzugefügt (`grafana/dashboards/salden.json`): Saldo-Verlauf
  je Konto plus Gesamtsumme, dazu ein Stat-Panel mit dem aktuellen Gesamtsaldo. Als
  Provisioning-Vorlage (`grafana/provisioning/`) für Neuinstallationen ohne vorhandenes
  Grafana; auf diesem Host stattdessen live über die Grafana-API in die bereits laufende
  Grafana-Instanz importiert und gegen echte Daten verifiziert (3 Konten + Gesamtlinie,
  Summe stimmt exakt).
- Fetch-Dienst läuft jetzt auf Host-Port 8750 statt 8080 (Portkonflikt mit einem anderen
  Dienst auf diesem Host), siehe `docker/docker-compose.yml`.

## 0.5.0 – Pagination und Fehlerdiagnose

- HTTP-Fehlerantworten werden jetzt inklusive Response-Body geloggt (comdirect liefert dort
  präzise Fehlercodes/-texte) statt nur den nackten Statuscode zu werfen.
- Dadurch gefunden und behoben: `paging-first > 0` scheitert mit 422 ("Paging is only valid
  for booked account transactions"), wenn `transactionState` nicht explizit auf `BOOKED`
  gesetzt ist (Default ist `BOTH`). Passt ohnehin zu unserem Modell – nur gebuchte,
  endgültige Umsätze werden historisiert.
- Live bestätigt: Pagination funktioniert über mehrere Seiten (ein Testkonto: 220 Umsätze
  über 11 Seiten erfolgreich abgerufen).
- Kleine Pausen zwischen Pagination-Seiten und zwischen Konten ergänzt, nachdem intensives
  Testen zu HTTP 429 (Rate-Limit) geführt hat.

## 0.4.0 – Weitere Live-Test-Funde

- Dapper schreibt Enum-Parameter standardmäßig als Zahl; die MySQL-ENUM-Spalten erwarten
  aber Text – `sync_log`-Inserts scheiterten mit „Data truncated for column 'status'“.
  Behoben durch explizite String-Konvertierung in `SyncLogRepository` (ein generischer
  Dapper-TypeHandler-Ansatz wurde zunächst versucht, erwies sich aber als wirkungslos für
  Parameter, da Dapper Enum-Parameter intern schon vor der Handler-Prüfung auf ihren
  zugrunde liegenden Zahlentyp reduziert – wieder entfernt).
- `bookingDate` kommt in der echten API als einfacher String, nicht wie dokumentiert als
  `{"date": ...}`-Objekt. `FlexibleDateConverter` akzeptiert jetzt beide Formen defensiv.
- Neuer Diagnose-Endpoint `GET /debug/summary` (Zeilenanzahl je Tabelle + letzte
  `sync_log`-Einträge) zur Verifikation ohne direkten DB-Zugriff.
- Live bestätigt: Salden (3 Konten) und Depotübersicht (1 Depot, Positionen inkl. ISIN/Name
  über `with-attr=instrument`) funktionieren vollständig gegen die echte API.

## 0.3.0 – Erste Live-Tests gegen die echte comdirect-API

- Fehlenden `Accept`-Header ergänzt (comdirect antwortete sonst mit 406 Not Acceptable) –
  in allen drei API-Clients.
- Hintergrunddienste starten jetzt sofort beim Start statt erst nach einem vollen Intervall
  zu warten (`RunOnceAsync`-Refactor, wiederverwendbar).
- Neuer manueller Trigger-Endpoint `POST /debug/fetch-now` für Tests ohne Wartezeit auf das
  konfigurierte Intervall.
- Login/Session/TAN-Flow erfolgreich gegen die echte API verifiziert (mehrere erfolgreiche
  End-to-End-Durchläufe inkl. PushTAN-Freigabe).

## 0.2.0 – Gegen offizielle comdirect-Doku verifiziert

Der Nutzer hat die offizielle comdirect REST API Dokumentation (Swagger, Postman-Collection,
PDF-Spezifikation) bereitgestellt; die 0.1.0-Implementierung wurde damit abgeglichen und
korrigiert:

- Endpunkt-Versionen korrigiert: Salden `v2` (statt `v1`), Depots `v3` (statt `v1`).
- TAN-Validierung/-Aktivierung sendet jetzt immer `sessionTanActive`/`activated2FA = true`
  im Body, statt den ggf. noch inaktiven, per GET abgerufenen Session-Zustand zu echoen.
- `AmountValue.value` wird korrekt als JSON-String gelesen (nicht als Zahl); `bookingDate`
  korrekt als verschachteltes `{"date": "..."}`-Objekt statt als flacher String.
- Depotpositionen: ISIN/Name kommen aus dem `instrument`-Attribut (`with-attr=instrument`
  Query-Parameter ergänzt), nicht von der Position selbst.
- Echte Pagination für Kontoumsätze (`paging-first`/`paging.matches`) statt nur einer Seite.
- Neues Feld `counterparty_iban` (Migration `0003`) als strukturiertes, zuverlässigeres
  Signal für die Erkennung interner Umbuchungen in der Kategorisierungslogik (Freitextsuche
  bleibt als Fallback).
- Sicherheits-Hinweis ergänzt und abgesichert: comdirect sperrt nach drei falschen
  TAN-Eingaben bzw. fünf TAN-Challenges ohne Einlösung den **gesamten** Online-Banking-Zugang
  (nicht nur die API) – `ComdirectAuthCoordinator.StartAsync` liefert bei bereits
  ausstehender Freigabe jetzt die bestehende Challenge zurück, statt eine neue anzufordern.
- Die comdirect-Auth-Flow-Mechanik (Session-TAN bleibt gültig, bis das letzte Access-/
  Refresh-Token-Paar abläuft) ist jetzt durch die offizielle Doku bestätigt, nicht mehr nur
  durch Community-Quellen gestützt.

## 0.1.0 – Erstes Grundgerüst

- Projektstruktur (Domain/Api/Data/Worker/Tests) gemäß `docs/konzept.md` Abschnitt 7 angelegt.
- Datenbankschema (`db/migrations/0001_init.sql`) und Basis-Kategorien/-Regeln
  (`db/migrations/0002_seed_categories.sql`) für die Kategorisierungslogik aus Abschnitt 6.
- comdirect-Authentifizierungsablauf (Erst-Login, TAN-Freigabe, session-gebundener Token,
  fortlaufender Refresh) inkl. `/auth/start` und `/auth/confirm` als manueller
  Freigabe-Trigger (Abschnitt 3).
- Hintergrunddienste für Salden-, Depotübersicht- und Kontoumsatz-Abruf inkl. Dedup und
  automatischer Kategorisierung (Abschnitt 5/6).
- Docker-Setup (Dockerfile, docker-compose mit optionalem Grafana) – MariaDB läuft extern.
