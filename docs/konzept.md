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

**Gültigkeitsdauer**: Übereinstimmend berichten mehrere unabhängige Quellen (comdirect-Community-Beiträge sowie mehrere quelloffene comdirect-API-Clients), dass der Access-Token ca. 10 Minuten und der Refresh-Token ca. 20 Minuten gültig ist – und dass **jede Erneuerung sowohl einen neuen Access- als auch einen neuen Refresh-Token liefert und damit die Gültigkeit verlängert** (sliding window). Solange der Fetch-Dienst durchgehend läuft und regelmäßig (empfohlen: alle ca. 8–9 Minuten, mit Sicherheitsmarge unter den 10 Minuten) einen Refresh durchführt, bleibt die Session ohne erneute TAN-Eingabe dauerhaft aktiv. Eine neue TAN-Freigabe ist nur nötig, wenn diese Kette unterbrochen wird – z. B. durch einen Neustart des Containers oder eine Downtime/Netzwerkunterbrechung, die länger als die Refresh-Token-Gültigkeit dauert.

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
| `portfolio_positions` | Einzelpositionen je Snapshot | ID, Snapshot-Referenz, WKN/ISIN, Bezeichnung, Stückzahl, Kurswert, Anschaffungswert, Gewinn/Verlust, Währung |
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
- **Asset-Allokation**: Verteilung des Depotwerts auf einzelne Positionen zum aktuellen Zeitpunkt (Kreis-/Balkendiagramm), zunächst je Einzelposition; eine Gruppierung nach Anlageklasse ist ein möglicher späterer Ausbau, sofern sich diese Klassifizierung zuverlässig aus den comdirect-Daten ableiten lässt.
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

Die Kategorisierung erfolgt beim erstmaligen Speichern eines Umsatzes. Werden die Regeln später erweitert oder korrigiert, ist eine erneute Kategorisierung des Bestands sinnvoll (nur für automatisch, nicht manuell zugeordnete Buchungen) – das genaue Vorgehen dafür ist Teil der technischen Umsetzung.

Ein kleines Startset an Regeln für die häufigsten, gut erkennbaren Fälle (Gehalt, Miete, gängige Versicherungen/Abo-Anbieter, interne Umbuchungen, Wertpapier-Order) reicht als MVP; der Rest landet zunächst im Fallback und wird iterativ verfeinert, sobald reale Umsatzdaten vorliegen.

**Phase 4 – baut auf den vorherigen Phasen auf**
- **Depot-Performance**: Wertentwicklung der Depots, bereinigt um Ein-/Auszahlungen. Erfordert sowohl die Depotwert-Historie (Phase 1) als auch die erkannten Cashflows (Phase 3), deshalb zuletzt.

Diese Priorisierung ist ein Vorschlag und kann vom Nutzer angepasst werden, insbesondere wenn eine der späteren Auswertungen inhaltlich wichtiger ist als der Implementierungsaufwand nahelegt.

## 7. Verzeichnisstruktur (Vorschlag)

```
comdirect-fetch/
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

**Datenbank-Schema-Version**: Jede strukturelle Änderung (neue Tabelle, neue Spalte, geänderter Typ) wird als eigene, fortlaufend nummerierte Migrationsdatei in `db/migrations/` abgelegt (Abschnitt 7). Bestehende Migrationen werden nie nachträglich verändert, nur neue ergänzt – append-only, analog zum historisierenden Datenprinzip aus Abschnitt 5. Ein Migrationswerkzeug führt die Historie der bereits angewendeten Migrationen in der Datenbank selbst; welches Werkzeug konkret zum Einsatz kommt, ist Teil der technischen Umsetzung (Abschnitt 9).

**Automatisches Versionieren während der Entwicklung**: Die Versionsnummern werden bei jeder relevanten Änderung automatisch angepasst und erhöht, nicht manuell durch den Nutzer gepflegt:
- Ändert eine Code-Änderung das Verhalten der Anwendung, wird die Anwendungsversion nach den obigen SemVer-Regeln erhöht und ein Eintrag in `CHANGELOG.md` ergänzt.
- Ändert eine Code-Änderung die Datenbankstruktur, wird statt einer Änderung an einer bestehenden Migration eine neue, fortlaufend nummerierte Migrationsdatei angelegt.

Diese Pflege übernimmt Claude Code während der Entwicklung selbstständig als Teil jeder Änderung; entsprechend wird dies als feste Vorgabe in der Projektdokumentation für Claude Code (`CLAUDE.md`) hinterlegt, sobald der Quellcode angelegt wird.

## 9. Offene Punkte / Annahmen

- **TAN-/Session-Gültigkeit** (Abschnitt 3): durch die offizielle comdirect-Doku bestätigt (Access-Token 599 Sek. ≈ 10 Min.; Session-TAN bleibt gültig, bis das letzte Access-/Refresh-Token-Paar abläuft). Erledigt.
- **Genaue Env-Variablen-Liste** (Abschnitt 4): wird final in der technischen Umsetzung festgelegt.
- **Priorisierung der Auswertungen** (Abschnitt 6): nach Datenabhängigkeit/Aufwand in vier Phasen vorgeschlagen; Bestätigung oder Anpassung durch den Nutzer steht noch aus.
- **Kategorisierung von Buchungstexten** (Abschnitt 6): Mechanismus (interne Umbuchung → strukturierter Umsatztyp → Muster-Regeln → Vorzeichen-Fallback → manuelle Korrektur) ist skizziert; die konkreten Start-Muster/Regeln sind noch zu erarbeiten, sobald reale Umsatzdaten vorliegen.
- **Umfang**: Konzept geht von einem einzelnen comdirect-Zugang aus (ein Nutzer, aber ggf. mehrere Konten/Depots innerhalb dieses Zugangs), keine Mandantenfähigkeit für mehrere getrennte comdirect-Zugänge.
- **MariaDB-Bereitstellung**: Datenbank samt Zugangsdaten wird vom Nutzer extern zur Verfügung gestellt; Betrieb/Deployment der Datenbank ist nicht Teil dieses Projekts.
- **Eindeutigkeit der Kontoumsatz-Referenz** (Abschnitt 5): Die offizielle Doku bezeichnet `reference` explizit als „unique reference code of the transaction“ – die Dedup-Annahme ist damit bestätigt. Offen bleibt nur, ob die Eindeutigkeit global oder je Konto gilt; der gewählte Schlüssel (`account_id`, `comdirect_reference`) ist für beide Fälle sicher.
- **Migrationswerkzeug** (Abschnitt 8): In der technischen Umsetzung wurde DbUp gewählt (führt die Historie angewendeter SQL-Skripte in der Zieldatenbank selbst). Erledigt.
- **Live-Verifikation gegen die echte comdirect-API** (Abschnitt 3): Login/Session/TAN-Flow, Salden, Depotübersicht (inkl. Positionen) und Kontoumsätze (inkl. Pagination über mehrere Seiten) wurden mit echten Zugangsdaten erfolgreich end-to-end getestet, siehe `CHANGELOG.md` 0.3.0–0.5.0. Erledigt.
- **Rate-Limiting** (Abschnitt 3): comdirect begrenzt die Anfragerate (HTTP 429 „rate.exceeded“) – bei intensivem Testen live beobachtet. Neben den proaktiven Pausen zwischen Pagination-Seiten/Konten gibt es jetzt ein echtes Retry-mit-Backoff (`ComdirectResilience`, siehe `CHANGELOG.md` 0.6.0) für Token- und Datenendpunkte. Erledigt für den Normalfall; ob das bei sehr großen Depots/Kontenzahlen im Dauerbetrieb ausreicht, bleibt zu beobachten.
- **Auswertung Phase 1 „Saldo-Verlauf je Konto“ + „Vermögensentwicklung“** (Abschnitt 6): vollständig als Grafana-Dashboard umgesetzt und mit echten Daten verifiziert (`grafana/dashboards/salden.json`, siehe `CHANGELOG.md` 0.6.0/0.7.0). Vermögensentwicklung nutzt eine korrelierte Subquery (jeweils letzter bekannter Depotwert je Salden-Zeitpunkt), da Salden- und Depotübersicht-Abrufe auf unterschiedlichen Intervallen laufen. In die auf dem Host bereits vorhandene Grafana-Instanz importiert; dieses Projekt betreibt bewusst kein eigenes Grafana (siehe README.md). Erledigt.
- **Auswertung Phase 2 „Asset-Allokation“ + „Einzelpositionsentwicklung“** (Abschnitt 6): als Grafana-Dashboard umgesetzt (`grafana/dashboards/depot.json`) und mit echten Daten verifiziert (21 Positionen über 4 Snapshots). Asset-Allokation weiterhin je Einzelposition, nicht nach Anlageklasse (siehe ursprüngliche Einschränkung oben). Erledigt.
