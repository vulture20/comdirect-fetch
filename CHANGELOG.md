# Changelog

Versionshistorie der Anwendung (Semantic Versioning, siehe `docs/konzept.md` Abschnitt 8).
Die Datenbank-Schema-Version wird separat über die fortlaufend nummerierten Dateien in
`db/migrations/` nachvollzogen.

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
