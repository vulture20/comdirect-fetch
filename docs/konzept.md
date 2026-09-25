# Konzept: comdirect-fetch

Konzeptionelles Briefing für ein Tool zum automatisierten Abruf und zur Historisierung von Finanzdaten aus der comdirect-API. Dieses Dokument beschreibt **was** gebaut werden soll und **wie** die Daten strukturiert sind – keine technische Umsetzung (kein Code, keine konkrete Bibliothekswahl).

## 1. Zielsetzung

Ein Dienst ruft in konfigurierbaren Intervallen Finanzdaten aus dem comdirect REST-API ab und schreibt sie in eine vom Nutzer bereitgestellte MariaDB-Datenbank – Salden und Depotbestände als fortlaufende Zeitreihe mit Zeitstempel (jede Abfrage ein neuer Datensatz), Kontoumsätze hingegen jeweils nur **einmalig** (dedupliziert), da es sich um unveränderliche, einmalige Buchungen handelt und keine Zeitreihe im eigentlichen Sinn. Ziel ist eine lückenlose Historie, auf deren Basis später Auswertungen (Vermögensentwicklung, Rendite, Cashflow etc.) erstellt werden können.

Ein comdirect-Zugang kann mehrere Konten und Depots umfassen; das Konzept berücksichtigt dies durchgängig (siehe Abschnitt 3 und 5).

## 2. Grobarchitektur

Ein Baustein ist Bestandteil dieser Implementierung, zwei weitere werden extern bereitgestellt:

- **Fetch-Dienst** (C#, läuft als Docker-Container): authentifiziert sich gegen die comdirect-API, ruft die konfigurierten Daten in den konfigurierten Intervallen für alle Konten/Depots des Zugangs ab und schreibt sie in die Datenbank. Läuft dauerhaft im Hintergrund (Scheduler-artig), nicht als einmaliger Batch-Job. Der einzige Baustein, der hier tatsächlich gebaut/betrieben wird.
- **Auswertung/Visualisierung**: separates, etabliertes BI-Tool (Grafana) greift direkt lesend auf die MariaDB zu und stellt Zahlen und Grafiken dar. Der Fetch-Dienst selbst liefert keine eigene Oberfläche – seine einzige Aufgabe ist zuverlässiges Abrufen und Speichern.

**MariaDB und Grafana sind nicht Bestandteil dieser Implementierung.** Die Datenbank wird vom Nutzer extern betrieben und bereitgestellt, inklusive Zugangsdaten; der Fetch-Dienst verbindet sich lediglich darauf (siehe Abschnitt 4). Grafana läuft ebenfalls extern (im konkreten Fall: eine bereits vorhandene Instanz beim Nutzer) – dieses Projekt liefert nur Dashboard-Definitionen als Code (`grafana/dashboards/`), die in eine bestehende Instanz importiert werden, keinen eigenen Grafana-Container. Es wird weder ein Datenbank- noch ein Grafana-Container aufgesetzt oder betrieben.

## 3. Datenabruf von der comdirect-API

Ein comdirect-Zugang kann mehrere Konten und mehrere Depots umfassen. Der Fetch-Dienst ruft daher zunächst die Liste aller Konten und Depots des Zugangs ab (Konten-/Depotübersicht-Endpoint) und holt anschließend für **jedes einzelne Konto bzw. Depot** die folgenden Daten:

- **Depotübersicht** – je Depot: Depotbestände inkl. Einzelpositionen (Wertpapiere, Stückzahl, Kurswert, Anschaffungswert etc.)
- **Salden** – je Konto der aktuelle Kontostand, je Depot der aktuelle Depotwert
- **Kontoumsätze** – je Konto die Buchungen (Ein-/Ausgänge, Datum, Betrag, Buchungstext)

Für jede Datenart ist das Abrufintervall unabhängig konfigurierbar, da sich die Datenarten unterschiedlich oft sinnvoll ändern (Umsätze/Salden ggf. häufiger als die vollständige Depotübersicht). Das Intervall gilt jeweils global für alle Konten/Depots eines Zugangs, nicht einzeln pro Konto.

### Session- und TAN-Handling (Nutzer-Ebene, recherchiert)

Der Authentifizierungsablauf der comdirect REST API für Privatkunden (OAuth2 Resource-Owner-Password-Flow) gliedert sich in mehrere Schritte:

1. **Initiale Anmeldung**: Der Fetch-Dienst fordert mit Client-ID/Secret und Zugangsnummer/PIN einen ersten Access-Token samt Refresh-Token an und legt eine Session an.
2. **TAN-Freigabe**: Die Session muss einmalig durch eine TAN bestätigt werden (z. B. photoTAN/PushTAN in der comdirect-App). Dieser Schritt erfordert eine aktive Bestätigung durch den Nutzer und lässt sich nicht automatisieren.
3. **Session-gebundener Token**: Nach der TAN-Freigabe tauscht der Dienst den Token gegen einen session-gebundenen Access-/Refresh-Token (sog. `cd_secondary`-Flow), mit dem die eigentlichen Konto-/Depot-Endpunkte aufgerufen werden.

Ein zusätzliches Client-Zertifikat der Anwendung ist für diesen Weg nicht erforderlich: Mehrere unabhängige, produktiv genutzte quelloffene comdirect-API-Clients kommen ausschließlich mit Client-ID/Secret, Zugangsnummer/PIN und TAN aus. Ein Zertifikat (eIDAS/QWAC) ist nur für die separate XS2A/PSD2-Schnittstelle für lizenzierte Drittanbieter dokumentiert, nicht für den direkten Zugriff auf den eigenen Account.

**Gültigkeitsdauer**: Übereinstimmend berichten mehrere unabhängige Quellen (comdirect-Community-Beiträge sowie mehrere quelloffene comdirect-API-Clients), dass der Access-Token ca. 10 Minuten und der Refresh-Token ca. 20 Minuten gültig ist – und dass **jede Erneuerung sowohl einen neuen Access- als auch einen neuen Refresh-Token liefert und damit die Gültigkeit verlängert** (sliding window). Solange der Fetch-Dienst durchgehend läuft und regelmäßig (empfohlen: alle ca. 8–9 Minuten, mit Sicherheitsmarge unter den 10 Minuten) einen Refresh durchführt, bleibt die Session ohne erneute TAN-Eingabe dauerhaft aktiv. Eine neue TAN-Freigabe ist nur nötig, wenn diese Kette unterbrochen wird – z. B. durch eine Downtime/Netzwerkunterbrechung, die länger als die Refresh-Token-Gültigkeit dauert. Ein Neustart des Containers unterbricht die Kette bei optional konfigurierter Token-Persistierung (Abschnitt 9, `CHANGELOG.md` 0.11.0) nicht mehr zwingend: der zuletzt gültige Token wird verschlüsselt in der DB gehalten und beim Start automatisch wiederhergestellt.

Daraus ergibt sich für das Konzept:

- Der Token-Refresh läuft als eigener, kontinuierlicher Hintergrundprozess unabhängig von den konfigurierten Datenabruf-Intervallen – er muss deutlich häufiger laufen als z. B. das Intervall für die Depotübersicht.
- Die eigentlichen Datenabrufe (Salden, Umsätze, Depotübersicht) nutzen einfach den jeweils aktuell gültigen Token und können ihr eigenes, gröberes Intervall haben.
- Nur beim allerersten Start bzw. nach einem session-brechenden Ausfall ist eine manuelle TAN-Freigabe durch den Nutzer nötig. Dieser Zustand sollte im `sync_log` klar erkennbar sein (Status „Freigabe erforderlich“), damit der Nutzer benachrichtigt werden kann.

**Update (verifiziert)**: Der Nutzer hat die offizielle comdirect REST API Dokumentation (Swagger, Postman-Collection, PDF-Spezifikation) bereitgestellt. Sie bestätigt den oben beschriebenen Mechanismus: Access-Token-Gültigkeit 599 Sekunden (~10 Minuten), und „Eine Session-TAN bleibt so lange gültig, bis das letzte Access/Refresh-Token seine Gültigkeit verliert.“ Die Sliding-Window-Refresh-Strategie ist damit offiziell bestätigt, nicht mehr nur durch Community-Quellen gestützt.

**Sicherheitshinweis (aus der offiziellen Doku, kritisch)**: comdirect sperrt nach **drei falschen TAN-Eingaben** bzw. **fünf TAN-Challenges ohne zwischenzeitliche Einlösung einer korrekten TAN** den **gesamten Online-Banking-Zugang** – nicht nur den API-Zugriff. Nach zwei Fehlversuchen über die API lässt sich der Zähler nur durch eine korrekte TAN-Eingabe auf der comdirect-Website zurücksetzen. Der `/auth/start`-Endpunkt darf daher nicht unkontrolliert wiederholt aufgerufen werden (die Implementierung liefert bei bereits ausstehender Freigabe die bestehende Challenge zurück statt eine neue anzufordern).

Quellen:
- [comdirect Community – REST API Schritt 2.3 Aktivierung TAN Session](https://community.comdirect.de/t5/website-apps/rest-api-schritt-2-3-aktivierung-tan-session/td-p/184745)
- [comdirect Community – REST API Schritt 2.4 Aktivierung einer Session-TAN](https://community.comdirect.de/t5/website-apps/rest-api-schritt-2-4-aktivierung-einer-session-tan/td-p/153737)
- [comdirect Community – REST-API cd_secondary Flow](https://community.comdirect.de/t5/website-apps/rest-api-cd-secondary-flow/td-p/336684)
- [python-comdirect-api (quelloffener Client, u. a. Autorefresh-Implementierung)](https://github.com/keisentraut/python-comdirect-api/blob/master/comdirect_api/session.py)

## 4. Konfiguration über Umgebungsvariablen

Alle Einstellungen werden dem Container ausschließlich über Umgebungsvariablen übergeben. Vorgeschlagene Kategorien:

**comdirect-Zugangsdaten (Nutzer-Ebene)**
- Client-ID, Client-Secret (OAuth2-API-Zugang)
- Zugangsnummer, PIN (Nutzer-Login)

**Abruf-Steuerung**
- Intervall Depotübersicht
- Intervall Salden
- Intervall Kontoumsätze
- Modus der Session-Erneuerung (siehe Punkt 3)

**Datenbank** (verbindet auf die vom Nutzer extern bereitgestellte MariaDB, deren Betrieb nicht Teil dieses Projekts ist)
- Host, Port, Datenbankname, Benutzer, Passwort

**Allgemein**
- Log-Level
- Zeitzone

Eine vollständige, exakte Liste der Variablen wird sinnvollerweise erst in der technischen Umsetzung festgelegt; die obige Gliederung zeigt die Kategorien, die abgedeckt werden müssen.

**Hinweis zur sicheren Ablage der comdirect-Zugangsdaten**: Client-ID/Client-Secret sowie
Zugangsnummer/PIN lagen ursprünglich als Klartext in einer lokalen `.env`-Datei und wurden
unverändert als Container-Umgebungsvariablen übergeben. Der in Abschnitt 10 beschriebene, nach
Sensibilität der Werte unterschiedlich starke Schutz ist seit `CHANGELOG.md` 0.12.0 umgesetzt:
Client-ID/Client-Secret über Docker-Compose-Secrets, Zugangsnummer/PIN über einen
Bootstrap-und-Wipe-Flow mit dediziertem Schlüssel.

## 5. Datenmodell (Vorschlag)

Grundprinzip: Es gibt drei unterschiedliche Arten von Daten, die unterschiedlich behandelt werden:

- **Zeitreihen** (Salden, Depotbestände): historisierend, append-only – jede Abfrage erzeugt einen neuen Datensatz mit Zeitstempel, nichts wird überschrieben oder ersetzt.
- **Kontoumsätze**: **einmalig**, nicht wiederholend. Jede einzelne Buchung wird genau ein Mal gespeichert, unabhängig davon, wie oft sie im Abrufintervall erneut von der comdirect-API geliefert wird (Überschneidungen zwischen Abrufzeitfenstern sind normal). Eindeutigkeit wird über die von comdirect vergebene Umsatz-Referenz sichergestellt (eindeutiger Schlüssel); ein bereits gespeicherter Umsatz wird beim nächsten Abruf erkannt und übersprungen, nicht erneut geschrieben. Ein Zeitstempel wird trotzdem geführt, markiert dann aber „wann erstmalig erfasst“, nicht „wann abgefragt“.
- **Stammdaten/Konfiguration** (Konten, Depots, Kategorien, Kategorisierungsregeln): werden nicht historisiert, sondern bilden den aktuellen Stand ab und werden bei Bedarf direkt aktualisiert bzw. gepflegt.

Stammdaten (Konten, Depots) werden separat von den bewegten Daten (Salden, Umsätze, Positionen) gehalten. Da ein Zugang mehrere Konten und Depots haben kann, sind `accounts` und `portfolios` als eigene Tabellen mit potenziell mehreren Zeilen ausgelegt; alle bewegten Daten referenzieren das jeweilige Konto bzw. Depot.

| Tabelle | Zweck | Wesentliche Felder |
|---|---|---|
| `accounts` | Stammdaten je Konto (ein Zugang kann mehrere Konten haben) | ID, comdirect-Konto-ID, IBAN, Kontoart, Bezeichnung, Währung |
| `account_balances` | Saldo-Historie je Konto (Zeitreihe) | ID, Konto-Referenz, Zeitstempel, Saldo, verfügbarer Betrag, Währung |
| `portfolios` | Stammdaten je Depot (ein Zugang kann mehrere Depots haben) | ID, comdirect-Depot-ID, Bezeichnung |
| `portfolio_snapshots` | Depotübersicht je Abruf und Depot (Zeitreihe) | ID, Depot-Referenz, Zeitstempel, Gesamtwert, Anschaffungswert, Währung |
| `portfolio_positions` | Einzelpositionen je Snapshot | ID, Snapshot-Referenz, WKN/ISIN, Anlageklasse (seit 0.15.0, GitHub-Issue #4), Bezeichnung, Stückzahl, Kurswert, Anschaffungswert, Gewinn/Verlust, Währung |
| `transactions` | Kontoumsätze je Konto, **je Buchung genau ein Datensatz** | ID, Konto-Referenz, comdirect-Umsatz-Referenz (eindeutig), Buchungstag, Valuta, Betrag, Währung, Buchungstext, Umsatztyp, Kategorie-Referenz, manuell kategorisiert (Ja/Nein), Zeitstempel Ersterfassung |
| `categories` | Kategorien für die Cashflow-Analyse/Kostenübersicht (Abschnitt 6) | ID, Name, Art (Einnahme/Ausgabe/Intern-Neutral) |
| `categorization_rules` | Priorisierte Muster-Regeln zur automatischen Kategorisierung (Abschnitt 6) | ID, Muster/Schlüsselwort, geprüftes Feld, Kategorie-Referenz, Priorität |
| `sync_log` | Protokoll jedes Abruflaufs | ID, Datenart, Konto-/Depot-Referenz (optional, da z. B. der Token-Refresh keinem einzelnen Konto zugeordnet ist), Anwendungsversion, Start, Ende, Status, Fehlermeldung |

`sync_log` dient der Nachvollziehbarkeit (inkl. Session-/Freigabe-Status aus Punkt 3).

Diese Struktur ist ein Vorschlag zur Diskussion – die endgültige Feinabstimmung (Datentypen, Indizes, exakte Feldnamen) erfolgt in der technischen Umsetzung.

## 6. Auswertungen (priorisiert)

Vorschläge für Auswertungen, die auf Basis der historisierten Daten möglich sind – jeweils sowohl als Zahl (aktueller Wert/Kennzahl) wie auch als Grafik (Zeitverlauf) darstellbar. Die Reihenfolge richtet sich nach Datenabhängigkeit (was ist am frühesten in nutzbarer Form vorhanden) und Umsetzungsaufwand, nicht nach freier Präferenz – so entsteht früh ein nutzbares Ergebnis, komplexere Auswertungen bauen auf einfacheren auf.

**Phase 1 – sobald Salden/Depotübersicht fließen (geringster Aufwand, reine Aggregation vorhandener Zeitreihen)**
- **Saldo-Verlauf je Konto**: einfache Zeitreihe direkt aus `account_balances`, dient auch als erster Funktionsnachweis der gesamten Pipeline.
- **Vermögensentwicklung**: Gesamtwert aller Konten und Depots im Zeitverlauf (Liniendiagramm) – Summe aus `account_balances` und `portfolio_snapshots`, kein zusätzliches Datenmodell nötig.

**Phase 2 – sobald Depotpositionen mehrfach erfasst sind**
- **Asset-Allokation**: Verteilung des Depotwerts auf einzelne Positionen zum aktuellen Zeitpunkt (Kreis-/Balkendiagramm), sowohl je Einzelposition als auch nach Anlageklasse gruppiert (seit 0.15.0, GitHub-Issue #4) – comdirect liefert die Klassifizierung zuverlässig über `instrument.staticData.instrumentType`.
- **Einzelpositionsentwicklung**: Kursverlauf und Gewinn/Verlust je Wertpapierposition über Zeit, aus `portfolio_positions`.

**Phase 3 – sobald Kontoumsätze mit ausreichender Historie vorliegen**
- **Cashflow-Analyse**: Ein- und Ausgänge je Konto und Zeitraum, aus den Kontoumsätzen abgeleitet. Erfordert die im Folgenden skizzierte Kategorisierung der Buchungstexte.
- **Kostenübersicht**: aus Umsatztexten erkennbare Gebühren/Ordergebühren, summiert über Zeit – nutzt dieselbe Kategorisierungslogik wie die Cashflow-Analyse und sollte direkt danach oder gemeinsam damit umgesetzt werden.

### Kategorisierungslogik für Buchungstexte (Grundlage für Phase 3)

Kontoumsätze kommen von comdirect als Freitext (Buchungstext/Verwendungszweck, Empfängername) plus ein paar strukturierten Feldern (u. a. ein grober Umsatztyp wie Lastschrift, Gutschrift, Dauerauftrag, Kartenumsatz, Wertpapierabrechnung). Für die Cashflow-Analyse und Kostenübersicht muss jede Buchung einer fachlichen Kategorie zugeordnet werden. Vorgeschlagener Mechanismus, mehrstufig von spezifisch nach unspezifisch:

1. **Interne Umbuchungen zuerst erkennen**: Buchungen zwischen den eigenen, in `accounts`/`portfolios` bekannten Konten/Depots (z. B. Tagesgeld ↔ Verrechnungskonto, Verrechnungskonto ↔ Depot) werden anhand der Gegenkonto-Referenz erkannt und als eigene Kategorie „Intern/Neutral“ markiert. Sie dürfen nicht als Einnahme *und* Ausgabe in die Cashflow-Analyse einfließen.
2. **Strukturiertes Feld nutzen**: Der von comdirect gelieferte grobe Umsatztyp (z. B. Wertpapierabrechnung, Dauerauftrag) liefert bereits ein erstes, zuverlässiges Signal und wird als erste Kategorisierungsebene verwendet, bevor Freitext überhaupt betrachtet wird.
3. **Regelbasiertes Muster-Matching auf Freitext**: Eine gepflegte, priorisierte Liste von Mustern (Schlüsselwörter/Textbausteine, z. B. im Buchungstext oder Empfängernamen) wird der Reihe nach geprüft, erste Übereinstimmung gewinnt. Beispiele für Startkategorien: Gehalt/Lohn, Miete/Wohnen, Versicherungen, Abonnements, Lebensmittel/Einzelhandel, Versandhandel, Ordergebühren, Kontoführungsgebühren. Die Regeln sind Daten, keine Programmlogik – sie sollen ohne Neu-Deployment des Diensts pflegbar sein.
4. **Fallback nach Vorzeichen**: Findet keine Regel eine Übereinstimmung, wird grob nach Betragsvorzeichen in „Sonstige Einnahme“ bzw. „Sonstige Ausgabe“ eingeordnet, statt die Buchung unkategorisiert zu lassen.
5. **Manuelle Korrektur mit Vorrang**: Der Nutzer kann eine automatisch zugewiesene Kategorie nachträglich korrigieren; eine solche manuelle Zuordnung wird als solche markiert und bei künftigen automatischen (Neu-)Kategorisierungen nicht überschrieben.

Die Kategorisierung erfolgt beim erstmaligen Speichern eines Umsatzes. Werden die Regeln später erweitert oder korrigiert, ist eine erneute Kategorisierung des Bestands sinnvoll (nur für automatisch, nicht manuell zugeordnete Buchungen) – umgesetzt über `POST /debug/recategorize` bzw. `comdirectctl.sh recategorize`, siehe Abschnitt 9.

Ein kleines Startset an Regeln für die häufigsten, gut erkennbaren Fälle (Gehalt, Miete, gängige Versicherungen/Abo-Anbieter, interne Umbuchungen, Wertpapier-Order) reicht als MVP; der Rest landet zunächst im Fallback und wird iterativ verfeinert, sobald reale Umsatzdaten vorliegen.

**Phase 4 – baut auf den vorherigen Phasen auf**
- **Depot-Performance**: Wertentwicklung der Depots, bereinigt um Ein-/Auszahlungen. Erfordert sowohl die Depotwert-Historie (Phase 1) als auch die erkannten Cashflows (Phase 3), deshalb zuletzt.

Diese Priorisierung ist ein Vorschlag und kann vom Nutzer angepasst werden, insbesondere wenn eine der späteren Auswertungen inhaltlich wichtiger ist als der Implementierungsaufwand nahelegt.

## 7. Verzeichnisstruktur (Vorschlag)

```
comdirect-fetch/
├── .github/
│   └── workflows/
│       └── docker-release.yml     # baut+pusht das Docker-Image nach ghcr.io bei jedem vX.Y.Z-Tag
├── src/
│   ├── ComdirectFetch.Api/        # Anbindung an die comdirect-API (Auth, Requests)
│   ├── ComdirectFetch.Domain/     # Domänenmodelle (Konto, Depot, Position, Umsatz, ...)
│   ├── ComdirectFetch.Data/       # Datenbankzugriff (Zugriff auf das in db/migrations/ versionierte Schema)
│   ├── ComdirectFetch.Worker/     # Scheduler / Hintergrunddienst für die Intervall-Abrufe
│   └── ComdirectFetch.Tests/
├── docker/
│   ├── Dockerfile
│   └── docker-compose.yml         # nur der Fetch-Dienst – MariaDB und Grafana laufen extern
├── db/
│   └── migrations/                # fortlaufend nummerierte, append-only Migrationsdateien (Schema-Version, siehe Abschnitt 8)
├── grafana/
│   └── dashboards/                # Dashboard-Definitionen, per Grafana-API in eine vorhandene Instanz importiert (kein eigener Grafana-Container)
├── scripts/
│   └── comdirectctl.sh             # TAN-Freigabe und Status-Abfrage, siehe README.md
├── docs/
│   └── konzept.md                 # dieses Dokument
├── .env.example
├── .gitignore
├── CHANGELOG.md                   # Versionshistorie der Anwendung (SemVer), ein Eintrag je Version
└── README.md
```

Die genaue Aufteilung innerhalb von `src/` ist in der technischen Umsetzung ggf. anzupassen; die Grobstruktur (API-Anbindung, Domänenmodell, Datenzugriff, Scheduler, Tests getrennt) soll aber erhalten bleiben.

## 8. Versionierung

### Quellcode (Git)

Der Quellcode wird mit Git versioniert. `.env`-Dateien mit echten Zugangsdaten dürfen nicht ins Repository gelangen – nur `.env.example` als Vorlage ohne echte Werte.

### Anwendungs- und Datenbank-Schema-Version

Anwendung und Datenbankstruktur erhalten je eine eigene, unabhängige Versionsnummer, da sich beide getrennt weiterentwickeln können – ein Schema-Stand kann von mehreren kompatiblen App-Versionen genutzt werden.

**Anwendungsversion** (Semantic Versioning, `MAJOR.MINOR.PATCH`):
- **PATCH**: Fehlerkorrektur ohne Verhaltensänderung
- **MINOR**: neue, abwärtskompatible Funktionalität (z. B. eine neue Auswertung, ein zusätzlich abgerufenes Feld)
- **MAJOR**: nicht abwärtskompatible Änderung (z. B. eine Datenmodell-Änderung, die bestehende Daten inkompatibel macht)

Die Versionsnummer wird zentral im Quellcode gepflegt, beim Start der Anwendung protokolliert und als Docker-Image-Tag verwendet, sodass jederzeit nachvollziehbar ist, welcher Stand läuft. Damit sich auch im Nachhinein zuordnen lässt, mit welcher App-Version ein Datensatz geschrieben wurde, wird die Anwendungsversion zusätzlich in jedem `sync_log`-Eintrag festgehalten (Abschnitt 5).

Ein Git-Tag `vX.Y.Z`, der der `AppVersion` entspricht, löst automatisiert Build und Veröffentlichung des passenden Docker-Images aus (`.github/workflows/docker-release.yml`, GitHub Container Registry) – getaggt mit der Versionsnummer und zusätzlich mit `latest`. Vorher müssen Build und Tests erfolgreich sein.

**Datenbank-Schema-Version**: Jede strukturelle Änderung (neue Tabelle, neue Spalte, geänderter Typ) wird als eigene, fortlaufend nummerierte Migrationsdatei in `db/migrations/` abgelegt (Abschnitt 7). Bestehende Migrationen werden nie nachträglich verändert, nur neue ergänzt – append-only, analog zum historisierenden Datenprinzip aus Abschnitt 5. Ein Migrationswerkzeug führt die Historie der bereits angewendeten Migrationen in der Datenbank selbst; welches Werkzeug konkret zum Einsatz kommt, ist Teil der technischen Umsetzung (Abschnitt 9).

**Automatisches Versionieren während der Entwicklung**: Die Versionsnummern werden bei jeder relevanten Änderung automatisch angepasst und erhöht, nicht manuell durch den Nutzer gepflegt:
- Ändert eine Code-Änderung das Verhalten der Anwendung, wird die Anwendungsversion nach den obigen SemVer-Regeln erhöht und ein Eintrag in `CHANGELOG.md` ergänzt.
- Ändert eine Code-Änderung die Datenbankstruktur, wird statt einer Änderung an einer bestehenden Migration eine neue, fortlaufend nummerierte Migrationsdatei angelegt.

Diese Pflege übernimmt Claude Code während der Entwicklung selbstständig als Teil jeder Änderung; entsprechend wird dies als feste Vorgabe in der Projektdokumentation für Claude Code (`CLAUDE.md`) hinterlegt, sobald der Quellcode angelegt wird.

## 9. Offene Punkte / Annahmen

- **TAN-/Session-Gültigkeit** (Abschnitt 3): durch die offizielle comdirect-Doku bestätigt (Access-Token 599 Sek. ≈ 10 Min.; Session-TAN bleibt gültig, bis das letzte Access-/Refresh-Token-Paar abläuft). Erledigt.
- **Genaue Env-Variablen-Liste** (Abschnitt 4): wird final in der technischen Umsetzung festgelegt.
- **Priorisierung der Auswertungen** (Abschnitt 6): nach Datenabhängigkeit/Aufwand in vier Phasen vorgeschlagen; Bestätigung oder Anpassung durch den Nutzer steht noch aus.
- **Kategorisierung von Buchungstexten** (Abschnitt 6): Mechanismus ist umgesetzt und mit echten Umsatzdaten verifiziert (siehe `CHANGELOG.md` 0.8.0, `db/migrations/0004_extend_categorization_rules.sql`) – 12 Start-Regeln (0002) plus 14 weitere Regeln/7 neue Kategorien anhand der ersten 16 realen Kontoumsätze. Die zuvor offene Frage nach dem genauen Vorgehen für eine erneute Kategorisierung des Bestands bei Regeländerungen ist ebenfalls geklärt: `POST /debug/recategorize` / `comdirectctl.sh recategorize` wenden den aktuellen Regelsatz erneut auf alle nicht manuell kategorisierten Umsätze an. Erledigt für den aktuellen Datenstand; weitere Muster werden iterativ ergänzt, sobald neue, bisher unbekannte Buchungstexte auftauchen.
- **Umfang**: Konzept geht von einem einzelnen comdirect-Zugang aus (ein Nutzer, aber ggf. mehrere Konten/Depots innerhalb dieses Zugangs), keine Mandantenfähigkeit für mehrere getrennte comdirect-Zugänge.
- **MariaDB-Bereitstellung**: Datenbank samt Zugangsdaten wird vom Nutzer extern zur Verfügung gestellt; Betrieb/Deployment der Datenbank ist nicht Teil dieses Projekts.
- **Eindeutigkeit der Kontoumsatz-Referenz** (Abschnitt 5): Die offizielle Doku bezeichnet `reference` explizit als „unique reference code of the transaction“ – die Dedup-Annahme ist damit bestätigt. Offen bleibt nur, ob die Eindeutigkeit global oder je Konto gilt; der gewählte Schlüssel (`account_id`, `comdirect_reference`) ist für beide Fälle sicher.
- **Migrationswerkzeug** (Abschnitt 8): In der technischen Umsetzung wurde DbUp gewählt (führt die Historie angewendeter SQL-Skripte in der Zieldatenbank selbst). Erledigt.
- **Live-Verifikation gegen die echte comdirect-API** (Abschnitt 3): Login/Session/TAN-Flow, Salden, Depotübersicht (inkl. Positionen) und Kontoumsätze (inkl. Pagination über mehrere Seiten) wurden mit echten Zugangsdaten erfolgreich end-to-end getestet, siehe `CHANGELOG.md` 0.3.0–0.5.0. Erledigt.
- **Rate-Limiting** (Abschnitt 3): comdirect begrenzt die Anfragerate (HTTP 429 „rate.exceeded“) – bei intensivem Testen live beobachtet. Neben den proaktiven Pausen zwischen Pagination-Seiten/Konten gibt es jetzt ein echtes Retry-mit-Backoff (`ComdirectResilience`, siehe `CHANGELOG.md` 0.6.0) für Token- und Datenendpunkte. Erledigt für den Normalfall; ob das bei sehr großen Depots/Kontenzahlen im Dauerbetrieb ausreicht, bleibt zu beobachten.
- **Auswertung Phase 1 „Saldo-Verlauf je Konto“ + „Vermögensentwicklung“** (Abschnitt 6): vollständig als Grafana-Dashboard umgesetzt und mit echten Daten verifiziert (`grafana/dashboards/salden.json`, siehe `CHANGELOG.md` 0.6.0/0.7.0). Vermögensentwicklung nutzt eine korrelierte Subquery (jeweils letzter bekannter Depotwert je Salden-Zeitpunkt), da Salden- und Depotübersicht-Abrufe auf unterschiedlichen Intervallen laufen. In die auf dem Host bereits vorhandene Grafana-Instanz importiert; dieses Projekt betreibt bewusst kein eigenes Grafana (siehe README.md). Erledigt.
- **Auswertung Phase 2 „Asset-Allokation“ + „Einzelpositionsentwicklung“** (Abschnitt 6): als Grafana-Dashboard umgesetzt (`grafana/dashboards/depot.json`) und mit echten Daten verifiziert (21 Positionen über 4 Snapshots). Asset-Allokation gibt es seit `CHANGELOG.md` 0.15.0 (GitHub-Issue #4) sowohl je Einzelposition als auch nach Anlageklasse gruppiert – comdirect liefert dafür `instrument.staticData.instrumentType` (SHARE/BONDS/SUBSCRIPTION_RIGHT/ETF/PROFIT_PART_CERTIFICATE/FUND/WARRANT/CERTIFICATE/NOT_AVAILABLE) bereits mit dem ohnehin genutzten `with-attr=instrument`-Query-Parameter zuverlässig mit – live gegen die echte API verifiziert (21 Positionen: 11 Aktien, 4 Zertifikate, 3 ETF, 3 Fonds). Erledigt.
- **Auswertung Phase 3 „Cashflow-Analyse“ + „Kostenübersicht“** (Abschnitt 6): als Grafana-Dashboard umgesetzt (`grafana/dashboards/cashflow.json`, siehe `CHANGELOG.md` 0.9.0) und mit echten Daten verifiziert (7 Monate). Erledigt. Aussagekraft hängt an der Kategorisierungsqualität (Abschnitt 6/9) – ein Großteil des Ausgabenvolumens liegt aktuell noch unter „Sonstige Ausgabe“, weitere Regeln werden iterativ ergänzt.
- **Auswertung Phase 4 „Depot-Performance”** (Abschnitt 6): in vereinfachter Form umgesetzt (`grafana/dashboards/depot-performance.json`, siehe `CHANGELOG.md` 0.10.0) – unrealisierter Gewinn/Verlust aus `total_value − acquisition_value`, mit echten Daten verifiziert. Bewusst NICHT umgesetzt: die im Konzept ursprünglich vorgesehene Bereinigung um externe Ein-/Auszahlungen. Dafür fehlt sowohl eine Depot↔Verrechnungskonto-Verknüpfung im Datenmodell als auch echte Wertpapier-Kauf/Verkauf-Umsätze zum Verifizieren (Kategorie „Ordergebühren” hat bislang 0 Treffer in den Live-Daten) – als Folge-Issue vorgemerkt, statt ungetestet zu raten.
- **Auth-Status über Neustarts persistieren** (Abschnitt 3): umgesetzt (siehe `CHANGELOG.md` 0.11.0, `db/migrations/0006_auth_token_store.sql`). Der Session-Token wird bei optional gesetztem `Comdirect__TokenEncryptionKeyBase64` AES-256-GCM-verschlüsselt in der DB abgelegt und beim Start automatisch wiederhergestellt (inkl. Refresh zur Gültigkeitsprüfung) – ein Neustart innerhalb der Refresh-Token-Gültigkeit braucht dann keine neue TAN-Freigabe mehr. Ohne gesetzten Schlüssel bleibt das Verhalten wie zuvor (In-Memory-only). Live verifiziert (Neustart nach echter TAN-Freigabe, Session danach direkt wieder `Authentifiziert`). Erledigt.
- **Sichere Ablage der comdirect-Zugangsdaten** (Abschnitt 4/10): umgesetzt (siehe
  `CHANGELOG.md` 0.12.0) – zweistufiger Schutz, Docker-Secrets für Client-ID/Secret,
  Bootstrap-und-Wipe-Flow mit dediziertem, dateibasiertem Schlüssel für Zugangsnummer/PIN.
  Bewusst zu unterscheiden vom bereits umgesetzten Punkt oben (der betrifft den Session-Token,
  nicht diese vier Werte). Live verifiziert. Erledigt.
- **Konsolidierungs- und Aufräumprozess für Zeitreihen-Daten** (Abschnitt 11): umgesetzt (siehe
  `CHANGELOG.md` 0.13.0) – Salden/Depot-Snapshots werden nach einer konfigurierbaren Rohdaten-Frist
  auf einen Wert/Tag konsolidiert, `sync_log` nach einer separaten Frist gelöscht, Kontoumsätze
  (`transactions`) bewusst nie angefasst. Beides komplett opt-in (nichts passiert ohne explizit
  gesetzte Zeiträume), automatisch per neuem Hintergrunddienst plus manuell auslösbar. Live
  verifiziert (synthetische Testdaten weit außerhalb des echten Datenbestands, echte Daten dabei
  nachweislich unangetastet). Erledigt.

## 10. Sichere Ablage der comdirect-Zugangsdaten (umgesetzt in 0.12.0)

**Ausgangslage**: Client-ID, Client-Secret, Zugangsnummer und PIN lagen ursprünglich ausschließlich als
Klartext in einer lokalen `.env`-Datei, wurden per `env_file:` unverändert als
Container-Umgebungsvariablen an den Dienst übergeben und binden über die Standard-Konfiguration in
`ComdirectApiOptions`. Das ist bewusst von der bereits umgesetzten Session-Token-Persistierung
(Abschnitt 3/9, `CHANGELOG.md` 0.11.0) zu unterscheiden: dort wird der Access-/Refresh-Token
verschlüsselt in der DB abgelegt, nicht diese vier Zugangsdaten.

**Bedrohungsmodell**: primär andere Prozesse/Nutzer auf dem geteilten Homelab-Host, die über
`docker inspect`, `docker exec … env`, `/proc/<pid>/environ` oder versehentliche Env-Var-Dumps in
Logs/Monitoring an die Werte gelangen könnten – nicht in erster Linie Zugriff auf die Datenbank
oder physischer Diebstahl der Festplatte.

**Differenzierung**: Zugangsnummer/PIN (der eigentliche Bank-Login) werden als deutlich sensibler
eingestuft als Client-ID/Client-Secret (reine API-Ebene) – das deckt sich mit dem bisherigen Umgang
in diesem Projekt (Client-ID/Secret wurden einmal direkt im Chat geteilt, die PIN nie). Beide Wert-
Paare bekommen deshalb bewusst unterschiedlich starken Schutz statt eines einheitlichen Mechanismus.

### A) Client-ID/Client-Secret: Docker-Compose-Secrets statt Umgebungsvariable

Statt über `env_file`/`environment:` werden Client-ID und Client-Secret als dateibasierte
Docker-Compose-Secrets bereitgestellt: zwei Dateien außerhalb von Git (`secrets/Comdirect__ClientId`,
`secrets/Comdirect__ClientSecret`, Dateirechte `600`, `secrets/` gitignored), über einen
`secrets:`-Block in `docker/docker-compose.yml` referenziert und dadurch als Dateien unter
`/run/secrets/…` im Container verfügbar statt als Umgebungsvariable. Auf Code-Seite liest der im
ASP.NET-Core-Shared-Framework bereits enthaltene `Microsoft.Extensions.Configuration.KeyPerFile`-
Provider (`builder.Configuration.AddKeyPerFile("/run/secrets", optional: true)` in `Program.cs`)
die Secret-Dateien ein und bindet sie automatisch in dieselben Konfigurationsfelder wie zuvor die
Umgebungsvariablen (Dateiname = Konfigurationsschlüssel, `__` als Trenner) – kein Eigenbau nötig.
`.env` bleibt als Fallback für lokale Entwicklung ohne Docker Compose bestehen.

Das schließt die Exposition dieser zwei Werte über `docker inspect`, `docker exec … env`,
`/proc/<pid>/environ` und versehentliche Env-Var-Dumps in Logs. Es schließt **nicht**, dass die
Werte weiterhin als Klartext-Dateien auf der Host-Platte liegen – wer direkten Dateizugriff auf den
Host hat, sieht sie trotzdem. Das ist für diese zwei, laut obiger Differenzierung weniger sensiblen
Werte bewusst akzeptiert; sie werden ohnehin bei jedem Token-Refresh (alle ~8 Minuten) im Klartext
im Prozessspeicher gebraucht. Betriebsaufwand: einmalige Umstellung, danach unverändert „Datei
bearbeiten, Container neu starten” für eine Rotation.

### B) Zugangsnummer/PIN: Bootstrap-und-Wipe-Flow mit dediziertem Schlüssel

Für die eigentliche Bank-Login (Zugangsnummer/PIN) gilt ein stärkerer Mechanismus: Klartext
existiert nur während eines einmaligen, expliziten Setup-Schritts auf der Platte, danach liegt
ausschließlich ein AES-256-GCM-verschlüsselter Blob in der Datenbank.

**Bootstrap-Schritt**: `scripts/comdirectctl.sh set-credentials` fragt Zugangsnummer und PIN
interaktiv ab (PIN per `read -s`, nie als Kommandozeilenargument, landet also nicht in
Shell-History/Prozessliste) und ruft `POST /admin/credentials` auf – auf derselben
Vertrauensebene wie die bestehenden `/debug/*`-Routen (keine zusätzliche Authentifizierung, der
Dienst ist nur innerhalb des Hosts auf Port 8750 erreichbar). `ComdirectFetch.Worker.Services.
CredentialProvider.SetCredentialsAsync` verschlüsselt Zugangsnummer/PIN mit der bestehenden
`ComdirectFetch.Domain.SecretEncryption` (AES-256-GCM), aber einem **neuen, dedizierten**
Schlüssel statt des Session-Token-Schlüssels, und legt das Ergebnis in der neuen Tabelle
`credential_store` ab (`db/migrations/0007_credential_store.sql`, `ComdirectFetch.Data.
CredentialRepository`, analog zu `auth_token_store`/`AuthTokenRepository`). Nach bestätigtem
Schreiben (Round-Trip-Entschlüsselung zur Verifikation, damit kein Zugriff stillschweigend
verloren geht) werden Zugangsnummer/PIN manuell aus der `.env`-Datei entfernt – Klartext
existiert danach nur noch für die kurze Dauer des Bootstrap-Aufrufs auf der Platte.
`ComdirectFetch.Api.ICredentialProvider` entkoppelt `ComdirectAuthClient` von
`ComdirectApiOptions.Username/Password`: die Werte werden jetzt zur Laufzeit (bei jedem Login,
nicht beim DI-Container-Bau) aufgelöst, mit `CredentialProvider` als produktiver Implementierung
und transparentem Fallback auf `ComdirectApiOptions`, falls kein Bootstrap durchgeführt wurde.

**Schlüssel-Aufbewahrung** (löst das Henne-Ei-Problem: der Schlüssel darf nicht wieder in `.env`
landen, sonst bringt das Entfernen der Zugangsdaten aus `.env` nichts): eine separate, eng
berechtigte Schlüsseldatei außerhalb von `.env` und außerhalb des Compose-Projektverzeichnisses auf
dem Host (Dateirechte `400`), per Docker-Bind-Mount (read-only, `/etc/comdirect-fetch/credential.key`
→ `/run/secrets/credential_key`) in den Container gereicht (`Comdirect__CredentialKeyFilePath`,
Standardwert passt zu diesem Mount-Ziel). Fehlt diese Datei, bleibt der ganze Mechanismus inaktiv
und der Dienst verhält sich wie zuvor (Zugangsnummer/PIN weiterhin aus `.env`/Konfiguration
gelesen) – kein Zwang zur Migration bestehender Deployments, analog zum bereits opt-in
gestalteten `Comdirect__TokenEncryptionKeyBase64`.
Der tatsächliche Sicherheitsgewinn hängt davon ab, dass diese Schlüsseldatei auf dem Host wirklich
enger berechtigt ist als `.env` – das ist eine Voraussetzung, keine automatische Eigenschaft.
Explizit geprüfte und abgelehnte Alternativen für die Schlüssel-Aufbewahrung: eine bei jedem Start
interaktiv einzugebende Passphrase (würde den unbeaufsichtigten Neustart des Diensts,
`restart: unless-stopped`, brechen), ein OS-Keyring/`systemd-creds` (neue Technologie/Lernaufwand,
da dieses Projekt bisher rein über `docker compose` ohne systemd-Units läuft) sowie ein dedizierter
Linux-User, der exklusiv die Schlüsseldatei besitzt (nur so gut wie die ohnehin schon vorhandene
Nutzer-/Gruppendisziplin auf dem geteilten Host, kein zusätzlicher Gewinn gegenüber der einfachen
Schlüsseldatei).

### Ergänzend, unabhängig von A/B (günstig, empfohlen, aber nicht Teil dieses Konzepts als Code)

- `.env`-Dateirechte auf dem Host verschärfen (`chmod 600`).
- Einmalig prüfen, wer auf dem Host Mitglied der `docker`-Gruppe ist – Docker-Gruppenmitgliedschaft
  ist praktisch root-äquivalent und damit der eigentlich maßgebliche Zugriffs-Hebel auf einem
  geteilten Host, unabhängig davon, wie die Zugangsdaten selbst abgelegt werden.

### Was dieses Konzept explizit nicht löst

Physischer Zugriff auf den Host ohne Festplattenverschlüsselung, jemand mit Root-Rechten auf dem
Host, sowie jemand mit direktem Lesezugriff auf die neue Schlüsseldatei selbst – all das bleibt
außerhalb des hier beschriebenen Schutzes und müsste, falls relevant, über Host-seitige Maßnahmen
(z. B. LUKS) abgedeckt werden.

## 11. Konsolidierungs- und Aufräumprozess für Zeitreihen-Daten (umgesetzt in 0.13.0)

**Ausgangslage**: `account_balances` und `portfolio_snapshots` (+ `portfolio_positions`) wachsen
mit jedem Abrufintervall unbegrenzt weiter (Standard-Intervalle: Salden alle 15 Minuten, Depot
stündlich – bei 3 Konten macht das rund 288 Salden-Zeilen/Tag, plus je nach Positionsanzahl grob
500–700 Positions-Zeilen/Tag). `sync_log` wächst mit jedem Abrufversuch aller Datenarten ähnlich
schnell. Auf Dauer (Monate/Jahre) summiert sich das zu erheblichem, größtenteils redundantem
Datenvolumen, ohne dass die volle Auflösung für Langzeit-Trends in Grafana tatsächlich gebraucht
wird. Bis 0.13.0 gab es keinerlei Aufräum-Mechanismus – alles wurde für immer aufbewahrt.

**Bewusst außerhalb des Umfangs**: `transactions` (das Finanz-Ledger – einzelne, unveränderliche
Buchungen, u. U. steuerlich relevant; "konsolidieren" würde hier echte Daten verfälschen oder
verlieren) sowie alle übrigen Tabellen (`accounts`, `portfolios`, `categories`,
`categorization_rules`, `auth_token_store`, `credential_store` – keine wachsenden Zeitreihen).

### Umfang und Konsolidierungsform

- **`account_balances` und `portfolio_snapshots`/`portfolio_positions`**: zweistufige,
  einstufig-konsolidierende Aufbewahrung. Nach einer konfigurierbaren Rohdaten-Frist werden pro
  Tag alle Zeilen bis auf eine gelöscht – behalten wird die zeitlich letzte Zeile des Tages (je
  `account_id`/`portfolio_id`, Tagesgrenze in UTC, konsistent mit `recorded_at`, das im Code
  bereits durchgängig `DateTimeOffset.UtcNow` ist). Für `portfolio_positions` bedeutet das: beim
  Löschen eines nicht mehr benötigten Snapshots werden dessen Positionszeilen im selben Schritt
  mitgelöscht (Fremdschlüssel-Constraint), die Positionen des behaltenen Tages-Snapshots bleiben
  unverändert erhalten. Optional – nur wenn zusätzlich konfiguriert – werden konsolidierte
  (1 Wert/Tag) Zeilen nach einer weiteren, separaten Frist vollständig gelöscht.
- **`sync_log`**: einfachere, einstufige Politik ohne Konsolidierung (ein Betriebs-/Diagnose-Log
  lässt sich nicht sinnvoll "verdichten") – Zeilen werden nach einer eigenen, separat
  konfigurierbaren Frist direkt gelöscht.
- **`transactions`**: unangetastet, siehe oben.

### Konfiguration – komplett opt-in

Analog zu `Comdirect__TokenEncryptionKeyBase64`/`Comdirect__CredentialKeyFilePath` (Abschnitt 9/10):
ohne explizit gesetzte Zeiträume passiert nichts, das heutige Verhalten (alles wird für immer
aufbewahrt) bleibt für bestehende Deployments unverändert. Drei unabhängige, optionale Zeiträume
(`src/ComdirectFetch.Worker/RetentionOptions.cs`):

- `Retention__RawDataRetentionDays` – Rohdaten-Frist für `account_balances`/`portfolio_snapshots`.
  Nicht gesetzt: keine Konsolidierung, aktuelles Verhalten bleibt bestehen.
- `Retention__ConsolidatedDataRetentionDays` – zusätzliche Frist, nach der konsolidierte
  (1 Wert/Tag) Zeilen komplett gelöscht werden. Nur wirksam, wenn `RawDataRetentionDays` ebenfalls
  gesetzt ist. Nicht gesetzt: konsolidierte Daten bleiben unbegrenzt erhalten.
- `Retention__SyncLogRetentionDays` – Frist für das Löschen alter `sync_log`-Zeilen, unabhängig
  von den beiden anderen Werten.

### Auslösung

`ComdirectFetch.Worker.Services.RetentionService` (`BackgroundService`, analog zu
`BalanceFetchService`/`PortfolioFetchService`/`TransactionFetchService`), läuft automatisch in
einem eigenen, seltenen Intervall (Standard: täglich, `Retention__IntervalSeconds`). Wie die
bestehenden Fetch-Dienste zusätzlich als konkreter Singleton registriert, damit ein manueller
Anstoß ohne Warten auf das Intervall möglich ist – `POST /debug/consolidate` (analog zu
`/debug/recategorize`) plus `comdirectctl.sh consolidate`. Berührt weder Session noch TAN.

### Sicherheit/Nachvollziehbarkeit

- Jeder Lauf wird wie die bestehenden Abrufe in `sync_log` protokolliert (neuer `data_kind`-Wert
  `Konsolidierung`, `db/migrations/0008_sync_log_add_konsolidierung.sql`), inklusive einer
  Zusammenfassung der konsolidierten/gelöschten Zeilenzahlen im Feld `error_message` (auch bei
  Erfolg, trotz des Feldnamens – pragmatische Wiederverwendung statt Schemaänderung nur für diesen
  Zweck) – damit über `GET /debug/summary`/`comdirectctl.sh status` nachvollziehbar, ohne
  direkten DB-Zugriff.
- Konsolidierung und Löschung laufen NICHT in einer expliziten DB-Transaktion (wie der Rest
  dieses Projekts, siehe `RetentionRepository`) – alle Operationen sind idempotent, ein Absturz
  zwischen zwei Schritten hinterlässt keinen dauerhaft inkonsistenten Zustand, da ein erneuter
  Lauf ihn einfach nachholt. Positionszeilen werden dabei immer vor ihrem Snapshot-Datensatz
  gelöscht (Fremdschlüssel-Reihenfolge).

### Auswirkung auf Grafana

Die bestehenden Dashboards (`salden.json`, `depot.json`) fragen `account_balances`/
`portfolio_snapshots` direkt ab; nach einer Konsolidierung zeigen ältere Zeiträume automatisch nur
noch die verdichtete Auflösung (1 Punkt/Tag statt alle 15/60 Minuten) – ohne Änderung an den
Dashboard-Queries selbst, da dieselben Tabellen/Spalten weiterverwendet werden. Erwartetes,
gewolltes Verhalten dieses Features, keine Nebenwirkung, die extra behandelt werden müsste.
