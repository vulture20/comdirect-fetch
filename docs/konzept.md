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
| `transactions` | Kontoumsätze je Konto, **je Buchung genau ein Datensatz** | ID, Konto-Referenz, comdirect-Umsatz-Referenz (eindeutig), Buchungstag, Valuta, Betrag, Währung, Buchungstext, Umsatztyp, Gegenkonto-IBAN, Gegenkonto-Name (seit 0.17.0), Kategorie-Referenz, manuell kategorisiert (Ja/Nein), Zeitstempel Ersterfassung |
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
3. **Regelbasiertes Muster-Matching auf Freitext**: Eine gepflegte, priorisierte Liste von Mustern (Schlüsselwörter/Textbausteine im Buchungstext, im Umsatztyp oder – seit 0.17.0 – im Empfängernamen, `RuleMatchField.CounterpartyName`/`transactions.counterparty_name`; comdirect liefert den Namen der Gegenseite über `remitter`/`deptor`/`creditor.holderName`, wichtig für Buchungen wie echte Überweisungen, deren Buchungstext nur den Verwendungszweck enthält, nicht den Empfänger) wird der Reihe nach geprüft, erste Übereinstimmung gewinnt. Beispiele für Startkategorien: Gehalt/Lohn, Miete/Wohnen, Versicherungen, Abonnements, Lebensmittel/Einzelhandel, Versandhandel, Ordergebühren, Kontoführungsgebühren. Die Regeln sind Daten, keine Programmlogik – sie sollen ohne Neu-Deployment des Diensts pflegbar sein (seit 0.16.0 über die Web-Oberfläche unter `/admin/rules/`, siehe Abschnitt 12).
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
- **Nicht alle Konten/Depots des Nutzers werden erfasst** (v0.23.1): der Nutzer hat mindestens 2
  weitere Depots samt zugehöriger Konten (IBANs `DE00123456780000000003`/`DE00123456780000000004`),
  die comdirect-fetch nicht kennt. Live bestätigt, kein Bug im Code: `GET /banking/clients/user/
  v2/accounts/balances` liefert unter der aktuellen Session-Autorisierung nur 3 Konten, `GET
  /brokerage/clients/{userId}/v3/depots` nur 1 Depot – beide Endpunkte laut Swagger ohnehin ohne
  Paging (liefern angeblich „all accounts“ in einem Rutsch), die zusätzlichen Konten/Depots fehlen
  also bereits in der rohen API-Antwort, werden nicht etwa herausgefiltert. Wahrscheinlichste
  Ursache: die ursprüngliche OAuth-/TAN-Autorisierung (`cd_secondary`-Token-Tausch, Abschnitt 3)
  hat den Zugriff auf eine Teilmenge der Konten/Depots beschränkt (typisches Muster bei
  PSD2/XS2A-artigen Consent-Flows – Zustimmung gilt ggf. nur für die zum Zeitpunkt der TAN-Freigabe
  ausgewählten Konten). Noch nicht verifiziert, ob eine erneute TAN-Freigabe (`POST /auth/start`)
  mit erweiterter Kontoauswahl das behebt – dafür ist eine echte, vom Nutzer initiierte
  TAN-Bestätigung nötig (Sicherheitsregel: kein automatisches/wiederholtes `/auth/start`). Zur
  Diagnose loggen `BalanceFetchService`/`PortfolioFetchService` jetzt bei jedem Abruf die Anzahl
  und IBANs/Depot-IDs der tatsächlich gelieferten Konten/Depots.
- **MariaDB-Bereitstellung**: Datenbank samt Zugangsdaten wird vom Nutzer extern zur Verfügung gestellt; Betrieb/Deployment der Datenbank ist nicht Teil dieses Projekts.
- **Eindeutigkeit der Kontoumsatz-Referenz** (Abschnitt 5, GitHub-Issue #11): Weder Swagger noch
  die deutsche PDF-Doku qualifizieren „unique reference code of the transaction“/„Eine eindeutige
  Referenznummer für diesen Umsatz“ näher – beide lassen offen, ob global oder je Konto. Per
  Analyse der echten Live-Daten (429 Umsätze über 3 echte Konten) mit hoher Zuversicht geklärt,
  aber bewusst nicht als hundertprozentig bewiesen dargestellt: (1) `comdirect_reference` ist im
  realen Datenbestand bereits kontoübergreifend eindeutig – 429 Zeilen, 429 verschiedene Werte,
  keine einzige Referenz taucht unter mehr als einem Konto auf; (2) das Format
  (`<16-stelliges alphanumerisches Präfix>/<numerische Sequenznummer>`, z. B.
  `022C296Z1STT9BUX/43176`) sieht nach einer bankinternen Clearing-/Abwicklungs-Batch-ID plus
  Sequenznummer aus, nicht nach etwas, das eine Kontonummer kodiert – solche Clearing-IDs werden
  typischerweise bankweit vergeben, nicht je Konto; (3) beide Doku-Varianten formulieren „unique“
  unqualifiziert, während vergleichbare Felder in derselben Doku, wo eine Einschränkung gemeint
  ist, das auch explizit benennen. Praktisch ändert das nichts: der gewählte Schlüssel
  (`account_id`, `comdirect_reference`) bleibt in jedem Fall sicher, daher keine Code-Änderung.
  Erledigt (mit dokumentierter Restunsicherheit, siehe oben).
- **Migrationswerkzeug** (Abschnitt 8): In der technischen Umsetzung wurde DbUp gewählt (führt die Historie angewendeter SQL-Skripte in der Zieldatenbank selbst). Erledigt.
- **Live-Verifikation gegen die echte comdirect-API** (Abschnitt 3): Login/Session/TAN-Flow, Salden, Depotübersicht (inkl. Positionen) und Kontoumsätze (inkl. Pagination über mehrere Seiten) wurden mit echten Zugangsdaten erfolgreich end-to-end getestet, siehe `CHANGELOG.md` 0.3.0–0.5.0. Erledigt.
- **Rate-Limiting** (Abschnitt 3): comdirect begrenzt die Anfragerate (HTTP 429 „rate.exceeded“) – bei intensivem Testen live beobachtet. Neben den proaktiven Pausen zwischen Pagination-Seiten/Konten gibt es jetzt ein echtes Retry-mit-Backoff (`ComdirectResilience`, siehe `CHANGELOG.md` 0.6.0) für Token- und Datenendpunkte. Erledigt für den Normalfall; ob das bei sehr großen Depots/Kontenzahlen im Dauerbetrieb ausreicht, bleibt zu beobachten.
- **Auswertung Phase 1 „Saldo-Verlauf je Konto“ + „Vermögensentwicklung“** (Abschnitt 6): vollständig als Grafana-Dashboard umgesetzt und mit echten Daten verifiziert (`grafana/dashboards/salden.json`, siehe `CHANGELOG.md` 0.6.0/0.7.0). Vermögensentwicklung nutzt eine korrelierte Subquery (jeweils letzter bekannter Depotwert je Salden-Zeitpunkt), da Salden- und Depotübersicht-Abrufe auf unterschiedlichen Intervallen laufen. In die auf dem Host bereits vorhandene Grafana-Instanz importiert; dieses Projekt betreibt bewusst kein eigenes Grafana (siehe README.md). Erledigt.
- **Auswertung Phase 2 „Asset-Allokation“ + „Einzelpositionsentwicklung“** (Abschnitt 6): als Grafana-Dashboard umgesetzt (`grafana/dashboards/depot.json`) und mit echten Daten verifiziert (21 Positionen über 4 Snapshots). Asset-Allokation gibt es seit `CHANGELOG.md` 0.15.0 (GitHub-Issue #4) sowohl je Einzelposition als auch nach Anlageklasse gruppiert – comdirect liefert dafür `instrument.staticData.instrumentType` (SHARE/BONDS/SUBSCRIPTION_RIGHT/ETF/PROFIT_PART_CERTIFICATE/FUND/WARRANT/CERTIFICATE/NOT_AVAILABLE) bereits mit dem ohnehin genutzten `with-attr=instrument`-Query-Parameter zuverlässig mit – live gegen die echte API verifiziert (21 Positionen: 11 Aktien, 4 Zertifikate, 3 ETF, 3 Fonds). Erledigt.
- **Auswertung Phase 3 „Cashflow-Analyse“ + „Kostenübersicht“** (Abschnitt 6): als Grafana-Dashboard umgesetzt (`grafana/dashboards/cashflow.json`, siehe `CHANGELOG.md` 0.9.0) und mit echten Daten verifiziert (7 Monate). Erledigt. Aussagekraft hängt an der Kategorisierungsqualität (Abschnitt 6/9) – ein Großteil des Ausgabenvolumens liegt aktuell noch unter „Sonstige Ausgabe“, weitere Regeln werden iterativ ergänzt.
- **Auswertung Phase 4 „Depot-Performance”** (Abschnitt 6): zwei Varianten. Die vereinfachte
  (`grafana/dashboards/depot-performance.json`, `CHANGELOG.md` 0.10.0) – unrealisierter
  Gewinn/Verlust aus `total_value − acquisition_value` – ist weiterhin die einzige, die die
  **volle** Kaufhistorie abdeckt (comdirects `acquisition_value` kennt den Anschaffungswert auch
  für Positionen, die vor Beginn des Trackings gekauft wurden). Zusätzlich seit 0.23.0
  (GitHub-Issue #12), überarbeitet in 1.1.0, die im Konzept ursprünglich vorgesehene Bereinigung um
  externe Ein-/Auszahlungen auf den Gesamtwert (Positionen + Guthaben des Verrechnungskontos) seit
  Trackingbeginn, siehe Abschnitt 13: Depot↔Verrechnungskonto-Verknüpfung
  (`defaultSettlementAccountId`/`settlementAccountIds`, live verifiziert im UUID-Format von
  `accounts.comdirect_account_id`), `transaction_type = 'Securities'` (nicht das dokumentierte
  „Wertpapierabrechnung”), und – erst nach den ersten echten Trades über das Verrechnungskonto
  möglich – die Erkenntnis, dass Käufe/Verkäufe dort intern sind und nur Geld von außen zählt.
  Offen: eine echte externe Überweisung auf/vom Verrechnungskonto liegt noch nicht vor (Annahme
  `Transfer`, nur per Unit-Test abgesichert), ebenso ein Sparplan-Kauf innerhalb des Trackingzeitraums
  (Lieferzeitpunkt der Anteile nicht direkt belegbar, angenommen Buchungstag).
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
- **Bedienoberfläche für Kategorien/Regeln** (Abschnitt 12): umgesetzt (siehe `CHANGELOG.md`
  0.16.0, GitHub-Issue #13) – kleine, vom Worker mitausgelieferte Web-Oberfläche unter
  `/admin/rules/` mit CRUD für `categories`/`categorization_rules`, Testen gegen Echtdaten
  (Einzel-Regel-Vorschau und volle Simulation vor dem Commit), zusätzlich per HTTP-Basic-Auth
  abgesichert (anders als die übrigen, bewusst ungeschützten `/debug/*`-Endpunkte, da hier
  dauerhafte Konfiguration geändert wird, nicht nur eine Aktion angestoßen). Live verifiziert.
  Erledigt.
- **Kategorisierungsregeln auch gegen den Empfänger** (Abschnitt 6): umgesetzt (siehe
  `CHANGELOG.md` 0.17.0) – neues `RuleMatchField.CounterpartyName`/`transactions.
  counterparty_name`, gespeist aus `remitter`/`deptor`/`creditor.holderName`, die comdirect
  schon immer mitgeliefert hat, aber bisher ungenutzt blieben (nur die IBAN wurde übernommen,
  siehe Abschnitt 9 oben). Wichtig für echte Überweisungen, deren Buchungstext nur den
  Verwendungszweck enthält, nicht den Empfänger-Namen – bei Kartenzahlungen steht der
  Händlername dagegen meist schon im Buchungstext. Alt-Umsätze werden beim nächsten regulären
  Abruf automatisch nachträglich befüllt (Upsert statt `INSERT IGNORE`, betrifft ausschließlich
  `counterparty_name`, nie die Kategorisierung). Live verifiziert. Erledigt.
- **„Nicht kategorisiert"-Übersicht + Grafana-Link** (Abschnitt 12): umgesetzt (siehe
  `CHANGELOG.md` 0.18.0) – neuer Button auf der Admin-Oberfläche listet Umsätze ohne echte
  Kategorisierung (`GET /admin/rules/api/uncategorized`), löst das manuelle DB-Nachsehen aus
  0.17.0 ab. Zusätzlich ein Dashboard-Link im Cashflow-Dashboard, der `/admin/rules/` direkt
  öffnet. Live verifiziert (289 Treffer korrekt aufgelistet). Erledigt.

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

### A) Client-ID/Client-Secret: dateibasiert statt Umgebungsvariable

Statt über `env_file`/`environment:` werden Client-ID und Client-Secret dateibasiert
bereitgestellt: zwei Dateien außerhalb von Git (`secrets/Comdirect__ClientId`,
`secrets/Comdirect__ClientSecret`, Dateirechte `600`, `secrets/` gitignored), unter
`/run/secrets/…` im Container verfügbar statt als Umgebungsvariable. Auf Code-Seite liest der im
ASP.NET-Core-Shared-Framework bereits enthaltene `Microsoft.Extensions.Configuration.KeyPerFile`-
Provider (`builder.Configuration.AddKeyPerFile("/run/secrets", optional: true)` in `Program.cs`)
die Dateien ein und bindet sie automatisch in dieselben Konfigurationsfelder wie zuvor die
Umgebungsvariablen (Dateiname = Konfigurationsschlüssel, `__` als Trenner) – kein Eigenbau nötig.
`.env` bleibt als Fallback für lokale Entwicklung ohne Docker Compose bestehen.

**Mounting-Mechanismus seit v1.0.0 geändert** (Abschnitt 14): ursprünglich über einen
Docker-Compose-`secrets:`-Block, seit dem Umstieg auf einen nicht-root-Container-Nutzer aber als
schreibgeschützter Bind-Mount in `docker/docker-compose.yml` – Grund: Compose respektiert
`uid`/`gid`/`mode` für `secrets:` nur im Swarm-Modus, unter normalem `docker compose up` bleiben
sie live bestätigt root:root und wurden für den jetzt unprivilegierten Prozess unlesbar. Ein
Bind-Mount übernimmt die Host-Dateirechte dagegen exakt; die drei betroffenen Dateien müssen daher
dem Container-App-Nutzer gehören (`chown 1654:1654 ...`, Details siehe Abschnitt 14). Am
grundsätzlichen Zweck (raus aus `docker inspect`/Umgebungsvariablen) ändert das nichts – Bind-Mount
und Compose-Secret sind beides Datei-basierte Mechanismen, keiner geht über Umgebungsvariablen.

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

## 12. Bedienoberfläche für Kategorien/Regeln (umgesetzt in 0.16.0)

**Ausgangslage**: `categories` und `categorization_rules` waren im Code zuvor rein lesbar
(`CategoryRepository.GetAllAsync`, `CategorizationRuleRepository.GetAllOrderedByPriorityAsync`
– keine Create/Update/Delete-Methoden). Jede bisherige Änderung am Regelsatz lief über eine neue,
append-only SQL-Migration (`0002_seed_categories.sql`, `0004_extend_categorization_rules.sql`,
`0005_fix_paypal_merchant_patterns.sql`) – funktional korrekt, aber unkomfortabel für iteratives
Anpassen, und ohne Möglichkeit, eine Änderung vor dem Anwenden gegen echte Buchungstexte zu prüfen.
`CategorizationLogic.Categorize` (Domain) ist dabei bereits eine reine, nebenwirkungsfreie Funktion
– das macht ein risikofreies Testen gegen echte, bereits gespeicherte Umsätze technisch einfach,
ganz ohne etwas zu schreiben.

### Editier-Weg: kleine, vom Worker mitausgelieferte Web-Oberfläche

Statt CLI/REST-CRUD oder eines Export/Edit/Import-Zyklus (beide ebenfalls erwogen, aber nicht
gewählt) eine schlanke HTML/JS-Seite (`wwwroot/admin/rules/index.html`), vom Worker selbst
ausgeliefert – kein Build-Toolchain/Framework, um die Komplexität gering zu halten (passend zur
bisherigen Projekt-Philosophie, keine Infrastruktur für hypothetische künftige Bedürfnisse
aufzubauen). Tabellen-Editor für beide Entitäten:

- **Kategorien**: Name, Typ (Einnahme/Ausgabe/InternNeutral).
- **Regeln**: Muster, geprüftes Feld (BookingText/TransactionType/CounterpartyName, seit 0.17.0), Kategorie, Priorität,
  optionaler Freitext-Kommentar (seit 0.19.0, siehe unten).

Pfad bewusst `/admin/rules/` statt des ursprünglich erwogenen bloßen `/admin/` – letzteres
kollidiert mit dem bereits bestehenden, bewusst unauthentifizierten `POST /admin/credentials`
(Abschnitt 10 B, Bootstrap-Schritt für Zugangsnummer/PIN, andere Vertrauensebene). CRUD-Endpunkte
unter `/admin/rules/api/categories`, `/admin/rules/api/rules` (`GET`/`POST`/`PUT`/`DELETE`), dafür
neue Repository-Methoden (`CreateAsync`/`UpdateAsync`/`DeleteAsync`/`GetByIdAsync`) in
`CategoryRepository`/`CategorizationRuleRepository`, die zuvor nur lesend waren.

**Prioritäts-Kollisionen**: `CategorizationLogic` prüft Regeln aufsteigend nach Priorität, „erste
Übereinstimmung gewinnt" – bei zwei Regeln mit identischer Priorität ist die Reihenfolge zwischen
ihnen undefiniert (abhängig von der SQL-Rückgabereihenfolge). `CategorizationRuleRepository.
PriorityInUseAsync` prüft das beim Anlegen/Ändern einer Regel und die Web-Oberfläche zeigt bei
einer Kollision eine Warnung (kein Hard-Block – eine Kollision ist nicht zwangsläufig falsch, nur
mehrdeutig).

### Testen gegen Echtdaten

Zwei Ebenen, beide rein lesend, schreiben nichts:

- **Einzel-Regel-Vorschau** (`POST /admin/rules/api/rules/preview`): prüft, ob das Muster
  case-insensitive als Teilstring im jeweiligen Feld (BookingText/TransactionType/CounterpartyName) vorkommt – wie
  `CategorizationLogic.Categorize` es täte, aber unabhängig von Priorität/anderen Regeln, damit die
  Kernfrage „matcht dieses Muster wirklich nur das, was ich meine?" direkt beantwortet wird, auch
  für eine noch nicht gespeicherte Kandidaten-Regel. Liefert die betroffenen echten Umsätze
  (Buchungstext, Betrag, aktuelle Kategorie), begrenzt auf die ersten 50 Treffer plus Gesamtzahl.
- **Volle Simulation** (`POST /admin/rules/api/rules/simulate`): neues
  `CategorizationService.SimulateRecategorizationAsync` wendet den kompletten, aktuell in der DB
  gespeicherten Regelsatz auf alle nicht manuell kategorisierten Umsätze an – dieselbe Berechnung
  wie `RecategorizeAllAsync`, nur ohne den `UpdateCategoryAsync`-Aufruf – und liefert einen Diff
  (alte → neue Kategorie je betroffenem Umsatz) zurück. Die Web-Oberfläche zeigt diesen Diff vor
  dem eigentlichen Commit; „Jetzt anwenden" löst das bereits bestehende
  `POST /debug/recategorize` aus, das dann tatsächlich schreibt.

### „Nicht kategorisiert"-Übersicht (seit 0.18.0)

Button auf der Admin-Oberfläche, der `GET /admin/rules/api/uncategorized` aufruft: listet
Umsätze ohne echte Kategorisierung (keine Kategorie oder nur der Vorzeichen-Fallback `Sonstige
Einnahme`/`Sonstige Ausgabe`), sortiert nach Buchungsdatum absteigend, begrenzt auf 100 Treffer
plus Gesamtzahl – löst das manuelle Nachsehen in der DB ab, um Kandidaten für neue Regeln zu
finden (genau das Vorgehen, das für die Empfänger-Regeln in 0.17.0 noch händisch nötig war).
Zusätzlich ein Dashboard-Link „Kategorien/Regeln bearbeiten →" im Cashflow-Dashboard
(`grafana/dashboards/cashflow.json`), der `/admin/rules/` in einem neuen Tab öffnet – von dort,
wo eine große Fallback-Menge beim Durchsehen der Finanzen typischerweise zuerst auffällt.

**Direktes Zuordnen (seit 0.21.0)**: pro Zeile eine Kategorie-Auswahl plus „Zuordnen"-Button –
nicht jeder Einzelposten rechtfertigt eine eigene, dauerhafte Regel. Neuer Endpunkt
`PUT /admin/rules/api/transactions/{id}/category` (Body `{ categoryId }`) ruft das bereits
bestehende `TransactionRepository.UpdateCategoryAsync(id, categoryId, manuallyCategorized: true)`
auf – bisher nur intern von `CategorizationService` genutzt. Die feste Markierung als manuell
ist hier bewusst nicht optional, da genau das der Zweck dieser Aktion ist: künftige
Regel-Anwendungen (`CategorizeNewTransactionsAsync`/`RecategorizeAllAsync`, beide basierend auf
`GetUncategorizedAsync`/`GetAllNonManuallyCategorizedAsync`) fassen diesen Umsatz danach nicht
mehr an. Eine unbekannte `categoryId` scheitert am bestehenden Fremdschlüssel
(`fk_transactions_category`) und wird als HTTP 400 statt eines rohen DB-Fehlers zurückgegeben.

### Freitext-Kommentar je Regel (seit 0.19.0)

Neue optionale Spalte `comment` auf `categorization_rules` (`VARCHAR(500) NULL`,
`db/migrations/0012_categorization_rules_add_comment.sql`), damit festgehalten werden kann,
warum eine Regel existiert – z. B. welcher konkrete Buchungstext sie ausgelöst hat, oder ein
Hinweis wie beim KAPITAL-PLUS/XTR-Padding-Fall, dass das Muster bewusst kurz gehalten wurde,
um innerhalb eines von comdirect aufgefüllten Textsegments zu bleiben. Rein informativ, geht
nicht in `CategorizationLogic.Categorize` ein und beeinflusst kein Matching. `POST`/
`PUT /admin/rules/api/rules` nehmen `comment` entgegen, die Web-Oberfläche zeigt ihn als
eigene, direkt inline editierbare Spalte in der Regeltabelle (Speichern beim Verlassen des
Feldes über den bestehenden `PUT`-Endpunkt, keine neue Bearbeiten-UI nötig) sowie als
optionales Feld beim Anlegen einer neuen Regel.

### Bestehende Regeln editieren (seit 0.20.0)

Bis 0.19.0 unterstützte die Regeltabelle in der Web-Oberfläche nur Anlegen und Löschen, obwohl
`PUT /admin/rules/api/rules/{id}` (alle Felder: Muster, Feld, Kategorie, Priorität, Kommentar)
serverseitig bereits seit 0.16.0 existierte – bislang nur außerhalb der UI nutzbar (z. B. direkt
per `curl`, wie beim KAPITAL-PLUS/XTR-Padding-Fix genutzt). Neuer „Bearbeiten"-Button pro Zeile
versetzt genau diese eine Zeile in einen Edit-Modus mit allen Feldern editierbar; „Speichern"
ruft den bestehenden `PUT`-Endpunkt auf (inkl. der bereits vorhandenen Prioritäts-
Kollisionswarnung), „Abbrechen" verwirft die Änderung ohne zu schreiben. Bewusst mit expliziten
Speichern/Abbrechen-Buttons statt automatischem Speichern bei jeder Feldänderung (anders als der
Kommentar, der einzeln und pro Zelle harmlos genug ist, um sofort bei jedem `onchange` zu
speichern) – bei gleichzeitig mehreren editierbaren Feldern pro Zeile (u. a. Priorität und
Kategorie) senkt ein expliziter Speichern-Schritt das Risiko, eine Regel durch einen Tippfehler
unbemerkt fehlzukonfigurieren. Keine Backend-Änderung nötig, rein die Web-Oberfläche.

### Neue Seite „Alle Umsätze" (seit 0.22.0)

Bisher zeigte die Admin-Oberfläche Umsätze nur ausschnitthaft (die „Nicht kategorisiert"-Liste,
begrenzt auf 100 Treffer, nur unkategorisierte). Neue, eigenständige Seite
`wwwroot/admin/rules/transactions/index.html`, erreichbar unter `/admin/rules/transactions/`
(bewusst als Unterpfad von `/admin/rules/`, damit sie automatisch von der bestehenden
Basic-Auth-Middleware erfasst wird – die prüft per `StartsWithSegments("/admin/rules")`, ein
neuer eigener Middleware-Eintrag wäre unnötig gewesen). Zeigt **alle** Umsätze (nicht nur
unkategorisierte) mit Datum, Buchungstext/Umsatztyp, Empfänger, Betrag, Konto und Kategorie,
inklusive Pagination (50 Zeilen/Seite) und Filterung nach: Freitext (Buchungstext/Empfänger,
gleiche Contains-Semantik wie überall sonst), Kategorie (inkl. Sonderwert „Ohne Kategorie"),
Konto, Buchungsdatum-Zeitraum sowie Betrag von/bis. Neuer Endpunkt
`GET /admin/rules/api/transactions/list` filtert weiterhin in-memory über
`TransactionRepository.GetAllAsync` (konsistent mit `/rules/preview` und `/uncategorized`, statt
eine neue dynamische SQL-Abfrage einzuführen) und paginiert erst danach mit `Skip`/`Take`.
Zusätzlich `GET /admin/rules/api/accounts` für den Konto-Filter. Die Kategorie ist pro Zeile
direkt änderbar über denselben `PUT /admin/rules/api/transactions/{id}/category`-Endpunkt wie
in der „Nicht kategorisiert"-Liste (0.21.0). Beide Admin-Seiten verlinken jetzt gegenseitig
aufeinander. Das gemeinsame CSS wurde aus `index.html` nach `wwwroot/admin/rules/admin.css`
ausgelagert, damit beide Seiten optisch konsistent bleiben, ohne es zu duplizieren.

### Zugriffsschutz

`/admin/rules/*` (Web-Oberfläche **und** die zugehörigen CRUD-/Test-Endpunkte) bekommt
HTTP-Basic-Auth mit einem Shared-Passwort aus `Admin__Password` – analog zum bisherigen Muster
„ein Secret in `.env`" (`Comdirect__TokenEncryptionKeyBase64`, `GRAFANA_TOKEN`). Kein neues
NuGet-Paket – ein einfacher Header-Check (`Authorization: Basic base64(user:pass)`,
`ComdirectFetch.Worker.AdminAuth`, konstante Vergleichszeit über
`CryptographicOperations.FixedTimeEquals`, Benutzername beliebig/ignoriert) in einer kleinen
eigenen Middleware. Ist `Admin__Password` nicht gesetzt, liefert `/admin/rules/*` durchgängig
HTTP 503 statt ungeschützt erreichbar zu sein – die Funktion ist dann schlicht nicht nutzbar,
nicht offen. **Seit v1.0.0 gilt dasselbe für `/debug/*`, `/auth/*` und `/admin/credentials`** –
die ursprüngliche Begründung hier ("die bleiben alle ungeschützt, da nur host-lokal erreichbar")
hat sich als falsche Annahme herausgestellt, siehe Abschnitt 14.

### Schutz der Spezial-Kategorien

`CategorizationService` verankert drei Kategorienamen fest im Code (`ComdirectFetch.Domain.
ProtectedCategoryNames`: „Intern/Neutral", „Sonstige Einnahme", „Sonstige Ausgabe" – Vorzeichen-
Fallback und interne Umbuchungserkennung hängen an genau diesen Namen). Die neuen CRUD-Endpunkte
lehnen Löschen und Umbenennen dieser drei Kategorien serverseitig mit einer klaren Fehlermeldung
ab (nicht nur im Frontend geprüft) – dieselbe zentral gepflegte Konstante wird von
`CategorizationService` und den neuen Endpunkten referenziert, damit beides nicht auseinanderläuft.
Löschen einer noch von Regeln/Umsätzen referenzierten Kategorie scheitert zusätzlich am
bestehenden Fremdschlüssel in der DB (kein Extra-Code nötig, `DELETE` schlägt einfach fehl).

### Was dieses Konzept nicht abdeckt

Bearbeitung der Konten-/Depot-Stammdaten, der `own_ibans`-Ableitung (kommt weiterhin automatisch
aus `accounts.iban`) oder sonstiger Konfiguration – ausschließlich `categories` und
`categorization_rules`, wie ursprünglich angefragt.

## 13. Depot-Performance bereinigt um externe Ein-/Auszahlungen (umgesetzt in 0.23.0, Modell überarbeitet in 1.1.0)

GitHub-Issue #12, Folge-Issue zu #2 (dort in vereinfachter Form gelöst, siehe Abschnitt 9). Bis
0.22.0 fehlten zwei Voraussetzungen: eine Depot↔Verrechnungskonto-Verknüpfung im Datenmodell und
echte Wertpapier-Kauf/Verkauf-Umsätze zum Verifizieren der Logik. Beides lag inzwischen vor.

### Depot↔Konto-Verknüpfung

comdirects Depot-API liefert `defaultSettlementAccountId`/`settlementAccountIds` (Swagger-Schema
`Depot`), bis dahin nicht erfasst (`DepotEntry` in `BrokerageModels.cs` kannte nur `depotId`/
`depotDisplayId`). Neu erfasst und gegen `accounts.comdirect_account_id` aufgelöst
(`PortfolioFetchService.UpdateSettlementAccountLinksAsync`), Ergebnis in neuer n:m-Tabelle
`portfolio_settlement_accounts` (`db/migrations/0013_portfolio_settlement_accounts.sql`,
`is_default`-Flag). **Live verifiziert**: Format ist identisch zu `accounts.comdirect_account_id`
(UUID), keine reine Kontonummer wie der Swagger-Wortlaut „account number" vermuten ließe – ohne
diesen Live-Check hätte die Migration auf einer falschen Annahme basiert. Ebenfalls live
bestätigt: das Girokonto ist selbst als (nicht-default) `settlementAccountId` registriert, weil
Sparplan-Käufe direkt darüber abgerechnet werden, nicht ausschließlich über das dedizierte
Verrechnungskonto – wichtig für die Klassifizierung unten. Bleibt keine Verknüpfung auflösbar (z. B.
unbekanntes Format), bleibt das Depot unverknüpft und liefert schlicht keine der neuen Kennzahlen
(kein Fehler).

### Modell seit 1.1.0 (Überarbeitung nach den ersten echten Trades auf dem Verrechnungskonto)

Die Fassung aus 0.23.0 wurde mit Daten gebaut, in denen es auf dem Verrechnungskonto nur
Dividenden gab; Käufe/Verkäufe kamen ausschließlich als Sparplan-Abbuchungen vom Girokonto vor und
wurden deshalb pauschal als „Geld von außen" gewertet. Sobald echte Trades über das Verrechnungskonto
vorlagen (28.09.: Kauf Munich Re −3.089,23 €, Verkauf BASF +341,87 €), zeigte sich, dass das nicht
trägt: der Kauf wurde komplett aus vorhandenem Guthaben bezahlt (Saldo 3.801,27 → 1.053,91 €, keine
einzige Überweisung), der Kapitaleinsatz sprang aber um 2.747,36 € und die TWR von +7,2 % auf −0,75 %.
Neues, im ursprünglichen Wortlaut von Issue #12 („nur externe Zu-/Abflüsse zählen") liegendes Modell:

- **Gesamtwert = Positionen (comdirect) + Guthaben des Standard-Verrechnungskontos.** Das Guthaben
  steckt bewusst im Wert – sonst sähen Verkäufe wie Entnahmen und Dividenden wie verschwunden aus.
  Nur das *Standard*-Verrechnungskonto (`portfolio_settlement_accounts.is_default`) zählt; das
  Girokonto ist zwar ebenfalls verknüpft (Sparplan), ist aber das Alltagskonto und gehört nicht zum
  Depotwert.
- **Kapitalbewegung ist nur Geld, das die Depot-Grenze von außen überschreitet.**
  `DepotCashflowClassifier.Classify(transaction, isDefaultSettlementAccount)`: `Securities` auf dem
  Verrechnungskonto = *intern* (Guthaben ↔ Wertpapiere), `Securities` auf einem allgemeinen Konto
  (Sparplan vom Girokonto) = extern (Kauf: Einzahlung, Verkauf: Auszahlung), `Transfer` auf dem
  Verrechnungskonto = extern (Vorzeichen), `Interest / Dividends` auf dem Verrechnungskonto = Ertrag
  (steckt im Guthaben), auf einem allgemeinen Konto = Auszahlung des Ertrags, alles andere = ignoriert
  (`Other`; Gründe wie zuvor: das Girokonto trägt auch Alltagsumsätze). Für `Transfer` auf dem
  Verrechnungskonto liegt noch **kein** echter Umsatz vor – reine Annahme, nur per Unit-Test abgesichert.
- **Bezugspunkt = erster Snapshot** (25.09.2026). Sein Gesamtwert ist das Startkapital
  (`net_invested_capital`), danach kommen nur noch externe Bewegungen hinzu. Behebt die frühere
  Schwäche, dass Kapital und Rendite bei einem lange bestehenden Depot nur die getrackten Käufe kannten
  (damals ca. 4090 %): Gewinn/Verlust und Rendite gelten jetzt ausdrücklich *seit Trackingbeginn*.
  Weiterhin gilt: nur `acquisition_value` deckt die volle Kaufhistorie ab.
- **TWR** = tagesverkettete Rendite auf den Gesamtwert, bereinigt um die externen Bewegungen; der
  Bezugspunkt ist der Startwert der Kette. Ohne externe Bewegungen ist sie mathematisch gleich der
  einfachen Rendite (live gegengeprüft: beide −1,0616 %).

### Live entdeckt: Positionen erscheinen bei Ausführung, das Guthaben später (und die Valuta noch später)

Zwei zunächst plausible Annahmen wurden von den echten Daten widerlegt – eine davon von mir selbst
falsch abgeleitet, weil eine Abfrage die Umlaute im Positionsnamen („Münchener Rückvers.") verfehlte
und ich daraus schloss, die Aktien seien noch nicht im Depot:

- **Nicht zur Valuta.** Ausführung Mo 28.09. morgens; die Munich-Re-Position stand ab dem Snapshot von
  28.09. 06:44 UTC im Depot, der gebuchte Saldo sank erst am 29.09. gegen 03:45, die Valuta ist der
  30.09. Ein „in Lieferung"-Zuschlag zum Gesamtwert (Spalte `securities_in_transit` aus Migration 0015,
  nie veröffentlicht, aber auf der Live-DB schon gelaufen – deshalb ein `DROP COLUMN` in 0016 statt
  einer nachträglichen Änderung, Migrationen sind append-only) zählte die Aktien doppelt (Gesamtwert
  41.317 statt 38.228 €).
  Deshalb Buchungstag (= Ausführungstag) als Zeitbezug für Bewegungen, nicht die Valuta – abweichend
  von der zunächst gewählten Variante „Valuta statt Buchungsdatum", deren Voraussetzung sich als falsch
  erwies.
- **Guthaben aus Buchungen zurückgerechnet statt aus Saldo-Stichproben.** Das Guthaben je Tag ist der
  aktuelle gebuchte Saldo minus alle Umsätze des Verrechnungskontos *nach* dem Tag
  (`DepotPerformanceCalculator`, Ledger-Rückrechnung; am Realbestand exakt: 1.053,91 + 2.747,36 =
  3.801,27 € = am 25.09. gemessener Saldo). Damit liegen Positionen und Guthaben am Ausführungstag
  zusammen; Restunschärfe nur innerhalb des Ausführungstags (Buchungstag ist tagesgenau,
  Snapshots stündlich, Tageswechsel in UTC). `available_amount` taugte nicht: es sinkt schon bei der
  Order-Erteilung (So abends), Tage vor der Ausführung.
- Der Gesamtwert springt dadurch nicht mehr um den Kaufbetrag; ein Kauf schlägt nur mit den echten
  Kosten zu Buche (hier ca. 27 € Gebühren).

### Berechnung und Speicherung

`ComdirectFetch.Domain.DepotPerformanceCalculator.Calculate` (reine Funktion, unit-getestet) liefert für
*jeden* Snapshot Guthaben, Kapital, kumulierte Dividenden und TWR. `PortfolioFetchService` ruft sie nach
jedem Snapshot für die komplette Historie auf und schreibt nur die Zeilen zurück, deren gespeicherte
Werte abweichen (`portfolio_snapshots.settlement_cash`, `net_invested_capital`, `dividends_received`,
`time_weighted_return_pct`; Migrationen 0014–0016). Dadurch ist die Berechnung selbstheilend (ein später
auftauchender Umsatz korrigiert auch die davorliegenden Zeilen) und der erste Lauf nach dieser
Modelländerung füllte die Historie ab dem Bezugspunkt automatisch neu auf (113 Snapshots).

Bewusst nicht gelöst: Zwischenstände am Tag der Ausführung (Buchungstag statt Uhrzeit, siehe oben);
Verkauf-Erlöse, die am Ausführungstag schon aus den Positionen verschwunden, aber noch nicht im Saldo
sind, sind durch dieselbe Rückrechnung abgedeckt; mehrere Depots bekommen keine gemeinsame Prozent-
Kennzahl (Prozentwerte lassen sich nicht summieren).

### Ein live entdeckter Dapper-Gotcha

`PortfolioSnapshotRepository` materialisierte ursprünglich in einen positional
`record PortfolioValuationPoint(DateTimeOffset Timestamp, decimal TotalValue)`. Das schlug live fehl
("A parameterless default constructor or one matching signature ... is required"): Dapper
materialisiert Records über deren Primärkonstruktor und verlangt dafür exakte CLR-Typ-Übereinstimmung
mit der SQL-Spalte (hier `DATETIME` → `DateTime`) – die sonst überall in diesem Projekt klaglos
funktionierende automatische `DateTime`-zu-`DateTimeOffset`-Konvertierung greift nur beim
property-setter-basierten Materialisieren normaler Klassen. Behoben durch Umstellung auf eine Klasse mit
settable Properties (heute `PortfolioSnapshotPerformanceRow`, gleiches Muster wie `Transaction`,
`SyncLogEntry` etc.). Ergänzt die bereits dokumentierten Dapper-Eigenheiten in `CLAUDE.md`
(Enum-Parameter).

### Dashboard

`grafana/dashboards/depot-performance.json` zeigt beide Sichten nebeneinander: die bestehenden
`acquisition_value`-basierten Panels oben (volle Kaufhistorie, ohne Guthaben), unten Gesamtwert vs.
Kapital, Gewinn/Verlust und Rendite seit Trackingbeginn sowie die TWR, jeweils mit Beschreibungen zu
Aussagekraft und Einschränkung.

## 14. Security-Review und Härtung (umgesetzt in 1.0.0, Breaking Change)

Auf Nutzeranfrage manuell durchgeführter Security-Review des gesamten Codes (SQL-Konstruktion,
Auth, Krypto, Admin-UI/XSS, Secrets-Handling, Deployment-Konfiguration), live gegen den
tatsächlich laufenden Container/Host geprüft, nicht nur den Quelltext gelesen.

### Fund: unauthentifizierte Endpunkte sind übers Netzwerk erreichbar

`/auth/*`, `/debug/*` und `POST /admin/credentials` waren bewusst unauthentifiziert, mit der
Begründung "Dienst ist nur host-lokal auf diesem Port erreichbar" (Abschnitt 10 B, 12). Live
widerlegt: `docker-compose.yml`s `ports: "8750:8080"` bindet auf alle Interfaces (`ss -tlnp`:
`0.0.0.0:8750`, `[::]:8750`), und die iptables-Regeln des geteilten Homelab-Hosts lassen diesen
Traffic von `0.0.0.0/0` durch, ohne Einschränkung. Jeder mit Netzwerkzugriff auf den Host konnte
also ohne Zugangsdaten `/admin/credentials` überschreiben, `/debug/summary` auslesen (Zeilenzahlen,
letzte 10 sync_log-Fehlermeldungen) oder `/auth/start`/`/debug/fetch-now`/`/debug/recategorize`/
`/debug/consolidate`/`/debug/notify-test` auslösen. `/admin/rules/*` war davon nicht betroffen
(dort griff Basic Auth bereits korrekt).

**Nutzerentscheidung**: nicht das Port-Binding ändern (Alternativvorschlag des Reviews), sondern
die bestehende Basic-Auth-Prüfung ausweiten. Ergebnis (`Program.cs`-Middleware, vorher nur
`/admin/rules` geprüft): dieselbe `Admin__Password`-Prüfung deckt jetzt zusätzlich `/auth`,
`/debug` und `/admin/credentials` ab – ein gemeinsames Passwort statt eines zweiten Secrets, wie
zuvor fail-closed (HTTP 503, nicht offen, wenn `Admin__Password` fehlt). `/health` bleibt bewusst
offen (nur Status/App-Version, keine sensiblen Daten, damit einfache Erreichbarkeits-/
Monitoring-Checks ohne Zugangsdaten funktionieren). `scripts/comdirectctl.sh` ermittelt das
Passwort jetzt selbst und hängt es an jeden Request an – Reihenfolge: Umgebungsvariable
`COMDIRECT_FETCH_ADMIN_PASSWORD`, sonst `Admin__Password` aus der `.env`-Datei neben dem Skript
(Repo-Root, wie von `docker-compose.yml` genutzt), sonst ein im Skript direkt eintragbarer
Literal-Fallback (`ADMIN_PASSWORD_LITERAL`) – deckt alle drei vom Nutzer genannten Varianten ab.

Bewusst als **Breaking Change** markiert (MAJOR-Bump auf 1.0.0): eine Installation ohne
konfiguriertes `Admin__Password` verliert mit diesem Update den bisherigen (unsicheren, aber
funktionierenden) Zugriff auf `/auth/*`/`/debug/*` komplett, bis ein Passwort gesetzt wird.

### Fund/Härtung: Container lief als root

Kein `USER` in `docker/Dockerfile`, daher root-Default des Basisimages. Behoben mit
`USER $APP_UID` – der bereits im `mcr.microsoft.com/dotnet/aspnet:10.0`-Basisimage enthaltene
unprivilegierte Nutzer (live geprüft: `app`, UID/GID 1654). Port 8080 braucht ohnehin keine
Root-Rechte (nur Ports < 1024 tun das).

**Live entdeckte Regression beim Umsetzen**: Docker Compose unterstützt `uid`/`gid`/`mode` für
`secrets:`-Einträge dokumentiert nur im Swarm-Modus. Unter normalem `docker compose up` wurden
diese Felder mit einer expliziten Warnung ignoriert (`secrets \`uid\`, \`gid\` and \`mode\` are
not supported, they will be ignored`, Compose v5.5.0) – die drei Secret-Dateien blieben
root:root mit den Rechten der Host-Quelldatei, für den jetzt unprivilegierten Prozess unlesbar.
Der Dienst stürzte beim ersten Testlauf sofort mit `System.UnauthorizedAccessException: Access to
the path '/run/secrets/Comdirect__ClientSecret' is denied` ab – Fund und Fix noch **vor**
Abschluss dieser Änderung, nicht erst danach bemerkt (siehe „Live verifiziert“-Ablauf unten).

Behoben durch Rückkehr von Compose-`secrets:` zu schreibgeschützten Bind-Mounts für
`Comdirect__ClientId`/`Comdirect__ClientSecret`/`credential_key` (Abschnitt 10 A/B) – Bind-Mounts
übernehmen Host-Dateirechte exakt 1:1, unabhängig vom Compose-Modus. Dafür müssen die drei
Host-Dateien dem Container-App-Nutzer gehören:

```bash
chown 1654:1654 secrets/Comdirect__ClientId secrets/Comdirect__ClientSecret
sudo chown 1654:1654 /etc/comdirect-fetch/credential.key
```

Rechte bleiben unverändert eng (`600`/`400`), nur der Eigentümer wechselt von root auf den
dedizierten App-Nutzer – strenger als vorher (zuvor konnte jeder root-Prozess auf dem Host
mitlesen), nicht lockerer. Die UID 1654 ist an das konkrete Basisimage-Tag gekoppelt
(`docker run --rm mcr.microsoft.com/dotnet/aspnet:10.0 sh -c 'id app'`) – bei einem
Image-Tag-Wechsel mit anderer App-UID müssen `USER $APP_UID` (bleibt dynamisch korrekt) und die
drei `chown`-Ziele (statischer Wert, muss manuell nachgezogen werden) im Blick behalten werden.

### Live verifiziert

Nach beiden Fixes: Prozess läuft als `app` (UID 1654, nicht root), alle drei Secret-Dateien
lesbar, comdirect-Session-Wiederherstellung funktioniert unverändert (`authState: Authentifiziert`
direkt nach Neustart). `/health` liefert ohne Auth 200; `/debug/summary`, `/auth/start`,
`/admin/credentials` liefern ohne Auth 401, mit falschem Passwort 401, mit korrektem Passwort
200 (bzw. den jeweiligen regulären Status je Endpunkt-Logik); `/admin/rules/api/categories` funktioniert unverändert mit dem
bestehenden Passwort (Regressionscheck). `comdirectctl.sh status` funktioniert Ende-zu-Ende mit
automatisch aus `.env` gelesenem Passwort.

### Weitere Review-Ergebnisse (keine Code-Änderung nötig)

Ebenfalls geprüft, kein Fund: SQL-Injection (alle Dapper-Queries parametrisiert, keine
String-Konkatenation), XSS in der Admin-Oberfläche (`escapeHtml` konsequent auf allen
DB-Werten, unescapte Interpolationen ausschließlich Zahlen/IDs oder über `textContent`),
AES-256-GCM-Implementierung (`SecretEncryption`, frischer Zufalls-Nonce pro Verschlüsselung),
Klartext-Logging von Secrets (keines gefunden), `comdirectctl.sh` (kein `eval`, JSON sauber über
`jq --arg`), Timing-sicherer Passwortvergleich (`CryptographicOperations.FixedTimeEquals`),
`.env`/`secrets/` nie committed. Geringfügig, seit v1.0.1 behoben (siehe Abschnitt 15): volle
IBANs wurden seit v0.23.1 bei jedem Saldenabruf geloggt (keine Zugangsdaten, aber direkt
identifizierende Finanzdaten in Logs).

## 15. Datenschutz-Review (umgesetzt in 1.0.1)

Auf Nutzeranfrage manuell durchgeführter Datenschutz-Review, ergänzend zum Security-Review in
Abschnitt 14 – Fokus: welche personenbezogenen Daten (auch von Dritten, nicht nur der
Nutzer:in selbst) verarbeitet, gespeichert, geloggt oder an Dritte übertragen werden.

### Zentraler Befund: Drittanbieter-Daten in `transactions`, unbegrenzt aufbewahrt

`transactions.counterparty_name`/`counterparty_iban` sowie oft auch `booking_text` enthalten
personenbezogene Daten der Gegenseite einer Buchung (Vermieter, Zahlungsempfänger, Auftraggeber) –
also Daten Dritter, nicht nur der Nutzer:in. Funktional begründet (Erkennung interner
Umbuchungen, `RuleMatchField.CounterpartyName`), keine willkürliche Übererfassung.
`RetentionRepository` lässt `transactions` bewusst und dokumentiert unangetastet ("das
Finanzbuch") – keine Konsolidierung, keine Löschung, unbegrenzte Aufbewahrung, kein Mechanismus,
um Daten zu einem einzelnen Umsatz/einer einzelnen Gegenseite gezielt zu löschen. Für ein privates,
selbst gehostetes Tool über die eigenen Finanzen kommt vermutlich die Haushaltsausnahme (Art. 2
Abs. 2 lit. c DSGVO) in Betracht – das ist eine rechtliche Einschätzung, die dieses Dokument nicht
abschließend trifft, nur die Fakten dazu festhält. Bewusst nicht umgesetzt (kein Nutzerauftrag):
eine gezielte Lösch-Möglichkeit für einzelne Umsätze/Gegenseiten.

### Behoben: IBAN-Logging maskiert (v1.0.1)

`BalanceFetchService` loggte seit v0.23.1 volle IBANs bei jedem Saldenabruf. Logdateien können
andere Aufbewahrungs-/Zugriffsregeln haben als die DB (z. B. zentrales Log-Aggregations-System auf
dem Host), daher potenziell breitere Exposition als nötig. Neue, reine
`ComdirectFetch.Domain.IbanMasking.Mask` (unit-getestet, gleiches Muster wie `TextDecoding`/
`SecretEncryption`) zeigt jetzt nur Länderkennung + Prüfziffer sowie die letzten 4 Stellen
(`DE00...0099`) – reicht zur Unterscheidung von Konten in Logzeilen, ohne die volle Kontonummer
preiszugeben. Der Fallback auf `AccountId` (falls keine IBAN vorliegt) bleibt unmaskiert – das ist
die interne comdirect-UUID des Kontos, keine Kontonummer, und war nicht Gegenstand der Anfrage.

### Weitere Befunde (keine Code-Änderung, nur festgehalten)

- **Rohe comdirect-Fehlertexte in `sync_log.error_message`**: `HttpResponseExtensions.
  EnsureSuccessWithBodyAsync` hängt den kompletten Response-Body einer fehlgeschlagenen Anfrage an
  die Exception-Message – landet dauerhaft in `sync_log`. Kein konkreter Fund von personenbezogenen
  Daten darin, aber ein Kanal, über den ungeplant mehr als nötig in dauerhaft gespeicherten Logs
  landen könnte.
- **Finanzdaten unverschlüsselt in der DB**: Nur Session-Token und Zugangsdaten werden
  AES-256-GCM-verschlüsselt (Abschnitt 9/10). Kontostände, Umsätze, Depotpositionen liegen im
  Klartext in MariaDB – Schutz hängt vollständig von der Absicherung der extern bereitgestellten
  DB ab, bewusst keine Anwendungs-seitige Verschlüsselung der eigentlichen Finanzdaten.
- **Grafana**: Dashboards zeigen Beträge, Kategorien und Gegenseiten-Namen im Klartext in der
  bereits existierenden, gemeinsam genutzten Grafana-Instanz ("BugZone", Abschnitt 2). Wer dort
  Zugriff auf diese vier Dashboards hat, liegt außerhalb der Kontrolle dieses Codes.
- **Positiv**: Benachrichtigungen (E-Mail/Webhook, `NotificationService`) enthalten nur eine
  generische "TAN-Freigabe erforderlich"-Nachricht plus technischem Fehlertext – keine Beträge,
  Kontonummern oder Umsatzdetails werden an externe Kanäle übertragen.
- **Positiv**: keine Analytics/Tracking, keine sonstige Datenübertragung an Dritte außer der
  comdirect-API selbst und den vom Nutzer konfigurierten Benachrichtigungskanälen.

## 16. Öffentliche Veröffentlichung (Repo + Docker-Image)

Auf Nutzerwunsch: GitHub-Repo und Docker-Image (`ghcr.io/vulture20/comdirect-fetch`) öffentlich
gemacht. Vorbereitend zwei Schritte:

### Lizenz

`LICENSE` (GNU Affero General Public License v3.0-or-later), Copyright (C) 2026 Thorsten
Schröpel – bewusst AGPL statt einer permissiveren Lizenz (MIT/Apache), da sie bei
netzwerkbasierter Nutzung (nicht nur bei Weitergabe) zur Offenlegung des Quellcodes einer
veränderten Version verpflichtet. Offizieller Lizenztext über die GitHub-Licenses-API bezogen
(`gh api /licenses/agpl-3.0`), nicht aus dem Gedächtnis rekonstruiert. `README.md` verweist im
neuen Abschnitt „Lizenz" darauf.

### Bereinigung der Git-Historie

Vor der Veröffentlichung wurde die komplette Historie (36 Commits, 18 Tags) auf personenbezogene
Daten geprüft, die im Rahmen dieser Session versehentlich in Dokumentation/Tests committet
wurden (Datenschutz-Review, Abschnitt 15): zwei echte IBANs zusätzlicher Depot-Konten in
`docs/konzept.md` §9, eine echte IBAN als Test-Fixture in `IbanMaskingTests.cs`, sowie der
volle Name des Nutzers und der Nachname eines Dritten (Vermieter) in `CHANGELOG.md` und einer
Commit-Message (v0.17.0). Bereinigt mit `git filter-repo --replace-text ... --replace-message
...` (Debian-Paket `git-filter-repo`) gegen eine Ersetzungsliste – Dateiinhalte UND
Commit-/Tag-Nachrichten, beide nötig (`--replace-text` allein deckt Commit-Nachrichten nicht ab,
live bestätigt). Alle betroffenen Werte durch erkennbar synthetische Platzhalter ersetzt
(`DE00123456780000000099` etc., „Max Mustermann", „Mustervermieter"), Historie danach
vollständig gegengeprüft (keine Treffer mehr, auch nicht über die GitHub-API auf dem bereits
gepushten Ergebnis). Anschließend zwingend `git push --force` für `main` und alle Tags
erforderlich – bestehende lokale Klone (inkl. des eigenen Arbeitsverzeichnisses) mussten danach
per `git fetch`+`git reset --hard`/`git fetch --force --tags` auf den neuen Stand gebracht
werden, da sich sämtliche Commit-Hashes ab dem betroffenen Commit (v0.17.0) geändert haben.

### Offene Punkte

- **Sichtbarkeit des Docker-Images**: konnte nicht automatisiert geprüft/umgestellt werden – der
  verfügbare `gh`-Token hat nicht den nötigen `packages`-Scope
  (`GET /user/packages/container/...` liefert 403). Muss manuell über die GitHub-Weboberfläche
  (Repo → Packages → comdirect-fetch → Package settings → Change visibility) geprüft/umgestellt
  werden.
- Diese Konzept-Datei sowie `CLAUDE.md`/`CHANGELOG.md` enthalten weiterhin ausführliche
  Beschreibungen der eigenen Systemlandschaft (Host-Setup, Grafana-Org-Name "BugZone",
  Kategorisierungsmuster echter, aber inzwischen anonymisierter Buchungen) – bewusst so belassen,
  da das für Dritte, die dieses Projekt als Vorlage für den eigenen Einsatz nutzen wollen,
  nützlicher Kontext ist und keine personenbezogenen Daten mehr enthält.
