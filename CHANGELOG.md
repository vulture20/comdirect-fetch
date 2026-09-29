# Changelog

Versionshistorie der Anwendung (Semantic Versioning, siehe `docs/konzept.md` Abschnitt 8).
Die Datenbank-Schema-Version wird separat über die fortlaufend nummerierten Dateien in
`db/migrations/` nachvollzogen.

## 1.1.0 – Depot-Performance: nur externe Ein-/Auszahlungen, Guthaben im Gesamtwert (Issue #12, Nachbesserung)

Die Auswertung aus 0.23.0 wurde mit Daten gebaut, in denen es auf dem Verrechnungskonto nur Dividenden
gab, und wertete deshalb jeden `Securities`-Umsatz als Geld von außen. Die ersten echten Trades über das
Verrechnungskonto (28.09.: Kauf Munich Re −3.089,23 €, Verkauf BASF +341,87 €) widerlegten das: der Kauf
lief komplett aus vorhandenem Guthaben (3.801,27 → 1.053,91 €, keine Überweisung), der Kapitaleinsatz
sprang trotzdem um 2.747,36 € und die TWR von +7,2 % auf −0,75 %. Neues Modell (Details
`docs/konzept.md` Abschnitt 13):

- **Gesamtwert = Positionen + Guthaben des Standard-Verrechnungskontos**; nur Geld von außen ist eine
  Kapitalbewegung. `DepotCashflowClassifier.Classify` bekommt dafür `isDefaultSettlementAccount`
  (`Securities` auf dem Verrechnungskonto = intern, auf dem Girokonto = extern; `Transfer` auf dem
  Verrechnungskonto = extern; Dividenden = Ertrag). Die neue `DepotPerformanceCalculator.Calculate`
  ersetzt die drei alten Funktionen und liefert je Snapshot Guthaben, Kapital, Dividenden und TWR.
- **Startkapital statt Teilsumme**: der erste Snapshot (25.09.) ist der Bezugspunkt, sein Gesamtwert das
  Startkapital. Behebt die überzeichnete Rendite von ca. 4090 % bei einem lange bestehenden Depot;
  Gewinn/Verlust und Rendite gelten jetzt ausdrücklich *seit Trackingbeginn*.
- **Guthaben aus Buchungen zurückgerechnet** (aktueller Saldo minus spätere Umsätze) statt aus
  Saldo-Stichproben. Live belegt: comdirect zeigt die ausgeführte Order sofort in den Positionen
  (28.09. 06:44 UTC), der gebuchte Saldo folgt erst am 29.09. nachts, die Valuta ist der 30.09. Bewegungen
  werden deshalb zum **Buchungstag** angesetzt (nicht zur Valuta, wie zunächst gewählt – die
  Voraussetzung dieser Variante war falsch); ein zwischenzeitlicher „in Lieferung"-Ansatz hatte die Aktien
  doppelt gezählt (41.317 statt 38.228 €). Eine erste Aussage, die Aktien seien noch nicht im Depot,
  beruhte auf einer Abfrage, die Umlaute im Positionsnamen verfehlte.
- **Selbstheilend**: jeder Lauf berechnet alle Snapshots ab dem Bezugspunkt neu und schreibt nur
  abweichende Zeilen zurück (der erste Lauf füllte 113 Snapshots rückwirkend).
- Migrationen `0015` (`settlement_cash`, zusätzlich `securities_in_transit`) und `0016` (entfernt
  `securities_in_transit` wieder – 0015 war schon auf der Live-DB gelaufen, Migrationen sind append-only).
- Dashboard `depot-performance.json`: Gesamtwert (Positionen + Guthaben) vs. Kapital, Gewinn/Verlust und
  Rendite seit Trackingbeginn, TWR.

Live verifiziert (29.09.): Gesamtwert 38.228,38 € (37.174,47 Positionen + 1.053,91 Guthaben), Gewinn
−410,17 €, Rendite und TWR identisch −1,0616 % (ohne externe Bewegungen müssen sie übereinstimmen); um den
Munich-Re-Kauf kein Sprung mehr. Offen: eine echte externe Überweisung aufs/vom Verrechnungskonto und ein
Sparplan-Kauf im Trackingzeitraum liegen noch nicht vor – beides nur per Unit-Test abgesichert.

## 1.0.1 – IBAN-Logging maskiert (Datenschutz-Review)

`BalanceFetchService` loggte seit v0.23.1 volle IBANs bei jedem Saldenabruf. Neue, reine
`ComdirectFetch.Domain.IbanMasking.Mask` (unit-getestet) zeigt jetzt nur noch Länderkennung +
Prüfziffer sowie die letzten 4 Stellen (z. B. `DE00...0099`) – reicht, um Konten in Logzeilen
auseinanderzuhalten, ohne die volle Kontonummer in potenziell anders aufbewahrten/zugänglichen
Logdateien preiszugeben.

## 1.0.0 – Auth-Ausweitung + Container läuft nicht mehr als root (Breaking Change)

Reaktion auf einen Security-Review-Fund: `/auth/*`, `/debug/*` und `POST /admin/credentials`
galten als "eh nur host-lokal erreichbar" und blieben deshalb unauthentifiziert – live widerlegt,
`docker-compose.yml` bindet den Port auf alle Interfaces, die Firewall des geteilten Homelab-Hosts
lässt das durch. Zwei Änderungen, beide bewusst als **Breaking Change** markiert (MAJOR-Bump):

- **Admin__Password schützt jetzt auch `/auth/*`, `/debug/*` und `/admin/credentials`**
  (dasselbe Passwort wie `/admin/rules/*`, keine zweite Variable). Ohne konfiguriertes
  `Admin__Password` sind diese Endpunkte komplett deaktiviert (503) statt offen – wie zuvor bei
  `/admin/rules/*`. `/health` bleibt bewusst ungeschützt (nur Status/Version, für einfache
  Erreichbarkeitschecks). `scripts/comdirectctl.sh` ermittelt das Passwort jetzt automatisch:
  Umgebungsvariable `COMDIRECT_FETCH_ADMIN_PASSWORD`, sonst `Admin__Password` aus der
  `.env`-Datei neben dem Skript, sonst ein im Skript direkt eintragbarer Literal-Fallback.
- **Container läuft nicht mehr als root** (`docker/Dockerfile`: `USER $APP_UID`, der im
  `aspnet:10.0`-Basisimage bereits enthaltene unprivilegierte Nutzer, live geprüft UID/GID 1654).
  Dabei live entdeckt: Docker Compose unterstützt `uid`/`gid`/`mode` für `secrets:` nur im
  Swarm-Modus – unter normalem `docker compose up` wurden diese Felder ignoriert und der Dienst
  stürzte beim ersten Testlauf sofort mit "Access to the path ... is denied" ab, weil die
  root-only-Secret-Dateien für den jetzt unprivilegierten Prozess unlesbar waren. Behoben durch
  Wechsel von Compose-`secrets:` zurück zu schreibgeschützten Bind-Mounts (übernehmen Host-Rechte
  1:1) kombiniert mit `chown 1654:1654` der drei Secret-Dateien auf dem Host
  (`secrets/Comdirect__ClientId`, `secrets/Comdirect__ClientSecret`,
  `/etc/comdirect-fetch/credential.key`) – **Betreiberaktion nötig vor dem Upgrade**, siehe
  `docker/docker-compose.yml`-Kommentar. Rechte bleiben eng (600/400), nur der Owner wechselt von
  root auf den dedizierten App-Nutzer.

Live verifiziert nach dem Fix: Prozess läuft als `app` (UID 1654), alle drei Secret-Dateien
lesbar, comdirect-Session-Wiederherstellung funktioniert weiterhin, `/health` offen,
`/debug/summary`/`/auth/start`/`/admin/credentials` liefern ohne Auth 401, mit korrektem Passwort
200, `/admin/rules/*` unverändert funktionsfähig, `comdirectctl.sh status` funktioniert Ende-zu-Ende
mit automatisch aus `.env` gelesenem Passwort.

## 0.23.1 – Logging der Anzahl abgerufener Konten/Depots

`BalanceFetchService`/`PortfolioFetchService` loggen jetzt bei jedem Abruf, wie viele Konten/
Depots die comdirect-API zurückliefert (inkl. IBANs/Depot-Anzeige-IDs) – Diagnosehilfe, nachdem
sich zeigte, dass die API unter der aktuellen Autorisierung nur einen Teil der beim Nutzer
tatsächlich vorhandenen Konten/Depots liefert (live bestätigt: 3 von mindestens 5 Konten, 1 von
mindestens 3 Depots). Keine funktionale Änderung, reine Beobachtbarkeit.

## 0.23.0 – Depot-Performance bereinigt um externe Ein-/Auszahlungen (Issue #12)

Löst GitHub-Issue #12 (Folge-Issue zu #2): Depot↔Verrechnungskonto-Verknüpfung erfasst
(`defaultSettlementAccountId`/`settlementAccountIds`, live verifiziert im selben UUID-Format wie
`accounts.comdirect_account_id`; neue Tabelle `portfolio_settlement_accounts`,
`db/migrations/0013_...sql`). Zwei neue Kennzahlen je `portfolio_snapshots`-Eintrag
(`db/migrations/0014_...sql`: `net_invested_capital`, `dividends_received`,
`time_weighted_return_pct`), berechnet von der neuen, reinen `ComdirectFetch.Domain.
DepotPerformanceCalculator`/`DepotCashflowClassifier` (unit-getestet): eine einfache
Netto-Kapitaleinsatz-Methode und eine präzisere, tagesverkettete Time-Weighted Return.
Klassifizierung bewusst nur anhand `transaction_type` (`Securities`/`Interest / Dividends`), nicht
anhand des Kontos – ein erster, kontobasierter Ansatz zählte live fälschlich Alltagsausgaben auf
dem (auch als Verrechnungskonto registrierten) Girokonto als Kapitaleinsatz mit (8540,41 € statt
korrekt 898,15 €), gefunden und korrigiert vor dem Release. Zwei neue Dashboard-Panels-Reihen in
`grafana/dashboards/depot-performance.json`, klar von der bestehenden, weiterhin unveränderten
`acquisition_value`-basierten Ansicht abgegrenzt. **Wichtige Einschränkung**: Netto-Kapitaleinsatz-
Rendite ist nur für Kapitalbewegungen seit Trackingbeginn aussagekräftig – bei einem bereits lange
bestehenden Depot ergibt das aktuell eine stark überzeichnete Zahl (live: ca. 4090 %); die
Time-Weighted-Return-Kennzahl ist davon nicht betroffen. Details siehe `docs/konzept.md`
Abschnitt 13.

## 0.22.0 – Neue Admin-Seite "Alle Umsätze"

Neue Seite `/admin/rules/transactions/` neben der bestehenden Kategorien/Regeln-Seite: zeigt
**alle** Umsätze (nicht nur unkategorisierte) mit Datum, Buchungstext/Umsatztyp, Empfänger,
Betrag, Konto und Kategorie. Filter/Suche nach Freitext (Buchungstext/Empfänger), Kategorie
(inkl. "Ohne Kategorie"), Konto, Buchungsdatum-Zeitraum und Betrags-Min/Max, mit Pagination
(50 Zeilen/Seite). Kategorie ist pro Zeile direkt änderbar (nutzt den bestehenden
`PUT /admin/rules/api/transactions/{id}/category`-Endpunkt aus 0.21.0). Neue Endpunkte:
`GET /admin/rules/api/transactions/list` (paginiert, filterbar, In-Memory-Filterung wie bei
`/rules/preview`/`/uncategorized`) und `GET /admin/rules/api/accounts` (für den Konto-Filter).
Beide Seiten verlinken jetzt gegenseitig aufeinander. Das bisher inline in `index.html`
eingebettete CSS wurde nach `wwwroot/admin/rules/admin.css` ausgelagert, damit beide Seiten
gleich aussehen, ohne Duplizieren.

## 0.21.0 – Einzelne unkategorisierte Umsätze direkt zuordnen

Nicht jeder unkategorisierte Einzelposten rechtfertigt eine neue, dauerhafte Regel. Die
"Nicht kategorisiert"-Liste (seit 0.18.0) hat pro Zeile jetzt eine Kategorie-Auswahl plus
"Zuordnen"-Button. Neuer Endpunkt `PUT /admin/rules/api/transactions/{id}/category` setzt
`category_id` und markiert den Umsatz als `manually_categorized = true` (nutzt das bereits
bestehende `TransactionRepository.UpdateCategoryAsync`, bisher nur intern von
`CategorizationService` verwendet), damit künftige Regel-Anwendungen
(`CategorizeNewTransactionsAsync`/`RecategorizeAllAsync`) diese Zuordnung nicht mehr
überschreiben. Eine unbekannte `categoryId` scheitert am bestehenden Fremdschlüssel und wird
als HTTP 400 statt eines rohen DB-Fehlers zurückgegeben.

## 0.20.0 – Bestehende Kategorisierungsregeln editieren

Die Regeltabelle in der Admin-Oberfläche (`/admin/rules/`) unterstützte bisher nur Anlegen und
Löschen einer Regel, obwohl `PUT /admin/rules/api/rules/{id}` (alle Felder: Muster, Feld,
Kategorie, Priorität, Kommentar) seit 0.16.0 bereits existierte. Neuer „Bearbeiten"-Button pro
Zeile versetzt sie in einen Edit-Modus (alle Felder editierbar, explizite „Speichern"/
„Abbrechen"-Buttons statt automatischem Speichern bei jeder Änderung, um versehentliches
Ändern zu vermeiden) und ruft dann denselben, bereits bestehenden `PUT`-Endpunkt auf – keine
Backend-Änderung nötig. Zeigt weiterhin die bestehende Prioritäts-Kollisionswarnung.

## 0.19.0 – Kommentarfeld für Kategorisierungsregeln

Neue optionale Freitext-Spalte `comment` auf `categorization_rules`
(`db/migrations/0012_categorization_rules_add_comment.sql`, `VARCHAR(500) NULL`), damit
festgehalten werden kann, warum eine Regel existiert – rein informativ, fließt nicht in
`CategorizationLogic.Categorize` ein. `POST`/`PUT /admin/rules/api/rules` nehmen und liefern
`comment` jetzt mit zurück; die Admin-Oberfläche (`/admin/rules/`) zeigt eine neue „Kommentar“-
Spalte in der Regeltabelle, editierbar direkt inline (Änderung wird beim Verlassen des Feldes
per bestehendem `PUT`-Endpunkt gespeichert) sowie ein optionales Kommentarfeld beim Anlegen
einer neuen Regel.

## 0.18.0 – "Nicht kategorisiert"-Übersicht in der Admin-Oberfläche + Grafana-Link

Neuer Button „Nicht kategorisiert" auf der Admin-Oberfläche (`/admin/rules/`, Issue #13):
listet Umsätze ohne echte Kategorisierung – entweder gar keine Kategorie oder nur der
Vorzeichen-Fallback (`Sonstige Einnahme`/`Sonstige Ausgabe`) – inkl. Buchungstext, Empfänger/
Auftraggeber und Betrag, sortiert nach Buchungsdatum absteigend, begrenzt auf 100 von insgesamt
angezeigten Treffern. Neuer Endpunkt `GET /admin/rules/api/uncategorized`. Löst das bisherige
manuelle Nachsehen in der DB ab, um Kandidaten für neue Regeln zu finden (siehe 0.17.0).

Zusätzlich ein Dashboard-Link „Kategorien/Regeln bearbeiten →" im Cashflow-Dashboard
(`grafana/dashboards/cashflow.json`), der `/admin/rules/` in einem neuen Tab öffnet – von dort,
wo man beim Durchsehen der Finanzen eine große „Sonstige Ausgabe"/„Sonstige Einnahme"-Menge
bemerkt, direkt zur Admin-Oberfläche. URL geht vom Standardport 8750 auf demselben Host aus wie
Grafana; bei abweichender Netzwerktopologie im Dashboard-JSON anzupassen und erneut per
`scripts/grafana-setup.sh dashboards` zu pushen.

Live verifiziert: 289 aktuell unkategorisierte Umsätze über den neuen Endpunkt korrekt
aufgelistet (Paginierung auf 100 bestätigt), Dashboard-Link nach dem Push per Grafana-API
bestätigt.

## 0.17.0 – Kategorisierungsregeln auch gegen den Empfänger/Auftraggeber

Manche Buchungen ließen sich nicht korrekt kategorisieren, weil der Empfänger-Name nur
strukturiert vorlag, nicht im Buchungstext – typisch bei echten Überweisungen (der Buchungstext
enthält dort meist nur den Verwendungszweck), anders als bei Kartenzahlungen, wo der Händlername
meist im Buchungstext selbst steht. comdirect liefert den Namen der Gegenseite schon seit jeher
über `remitter`/`deptor`/`creditor.holderName` mit – bisher wurde daraus nur die IBAN
übernommen (`counterparty_iban`, 0.3.0), der Name blieb im bereits geparsten DTO ungenutzt.

- Neue Spalte `transactions.counterparty_name`
  (`db/migrations/0010_transactions_counterparty_name.sql`), befüllt in
  `TransactionFetchService` mit derselben Remitter/Debtor/Creditor-Auswahllogik wie schon bei
  der IBAN (Remitter bei Gutschrift, sonst Debtor/Creditor).
- Neuer `RuleMatchField.CounterpartyName`
  (`db/migrations/0011_categorization_rules_add_counterparty_name.sql` erweitert das
  `match_field`-ENUM) – bestehende Regeln bleiben unverändert, kein automatisches "auch gegen
  den Empfänger matchen" für bereits existierende BookingText-Regeln, um keine bestehenden
  Kategorisierungen unerwartet zu verschieben. Neue Option in der Web-Oberfläche
  (`/admin/rules/`, Issue #13) für Regel-Anlage und Einzel-Regel-Vorschau.
- `TransactionRepository.InsertIfNewAsync` von `INSERT IGNORE` auf ein gezieltes Upsert
  umgestellt (`ON DUPLICATE KEY UPDATE counterparty_name = ...`) – da comdirect bei jedem Abruf
  wieder die komplette verfügbare Historie liefert (kein "seit letztem Mal"-Cursor), backfillt
  das `counterparty_name` für alle bereits gespeicherten Alt-Umsätze automatisch beim nächsten
  regulären Abruf. `category_id`/`manually_categorized` werden dabei nie angetastet – bestehende
  Kategorisierung bleibt unberührt.
- 2 neue Unit-Tests für `CategorizationLogic` mit dem neuen Feld.

Live verifiziert: nach dem Deploy `fetch-now` ausgelöst, `counterparty_name` für 38 der 429
bestehenden Umsätze automatisch nachgetragen (Rest: Kartenzahlungen, für die comdirect keinen
strukturierten Empfänger liefert – bei denen steht der Händlername ohnehin meist schon im
Buchungstext). Anhand der jetzt sichtbaren Empfänger-Namen zwei neue, über die neue
Web-Oberfläche (Issue #13) angelegte Regeln, nach Bestätigung durch den Nutzer und Prüfung per
Simulation vor dem Commit:

- `Max Mustermann` (CounterpartyName) → „Intern/Neutral" – konsolidiert 11 Selbstüberweisungen
  (Tagesgeld-/Sparplan-Übertrag), von denen bisher nur 2 über die IBAN-basierte Erkennung als
  intern erkannt wurden, 9 aber in „Sonstige Einnahme“/„Sonstige Ausgabe“ landeten, weil dafür
  keine Gegenkonto-IBAN vorlag.
- `Mustervermieter` (CounterpartyName) → „Miete/Wohnen" – konsolidiert 2 weitere Buchungen mit dem
  bereits über die Dauerauftrag-Regel erkannten Vermieter (Nebenkostennachzahlung,
  Stromkostenerstattung nach Wasserschaden), deren Buchungstext den Vermieter-Namen nicht
  enthielt.

`POST /debug/recategorize` danach ausgeführt: 11 Umsätze tatsächlich neu kategorisiert, DB-Stand
bestätigt (11 Zeilen unter „Intern/Neutral“, 8 unter „Miete/Wohnen“ für diese beiden Empfänger).

## 0.16.0 – Bedienoberfläche für Kategorien/Regeln (Issue #13)

`categories`/`categorization_rules` waren im Code bisher rein lesbar – jede Änderung brauchte
eine neue append-only Migration, es gab keine Möglichkeit, ein neues Muster vorab gegen echte
Buchungstexte zu prüfen. Neue, vom Worker selbst ausgelieferte Web-Oberfläche
(`wwwroot/admin/rules/index.html`, kein Build-Toolchain/Framework) unter `/admin/rules/`:

- **CRUD** für Kategorien und Regeln (`CategoryRepository`/`CategorizationRuleRepository` haben
  jetzt `CreateAsync`/`UpdateAsync`/`DeleteAsync`/`GetByIdAsync`, zuvor nur `GetAll*`), inkl.
  weicher Warnung bei doppelt vergebener Regel-Priorität.
- **Testen gegen Echtdaten** auf zwei Ebenen, beide rein lesend:
  Einzel-Regel-Vorschau (`POST /admin/rules/api/rules/preview` – welche echten Umsätze ein
  Kandidaten-Muster träfe) und volle Simulation (`POST /admin/rules/api/rules/simulate` –
  neues `CategorizationService.SimulateRecategorizationAsync`, identische Berechnung wie
  `RecategorizeAllAsync`, aber ohne zu schreiben), mit „Jetzt anwenden" gegen das bestehende
  `POST /debug/recategorize`.
- **Zugriffsschutz**: `/admin/rules/*` (bewusst nicht das bereits belegte, andersartig
  vertrauensvolle `/admin/credentials`, Issue #5) verlangt HTTP-Basic-Auth über ein neues
  `Admin__Password` (`ComdirectFetch.Worker.AdminAuth`, unit-getestet, konstante Vergleichszeit) –
  ohne gesetztes Passwort liefert `/admin/rules/*` durchgängig HTTP 503 statt ungeschützt
  erreichbar zu sein.
- **Schutz der Spezial-Kategorien**: neues `ComdirectFetch.Domain.ProtectedCategoryNames`
  (zentral von `CategorizationService` und den neuen Endpunkten referenziert) lehnt Löschen/
  Umbenennen von „Intern/Neutral", „Sonstige Einnahme", „Sonstige Ausgabe" serverseitig ab.

Live verifiziert gegen die echte DB: Kategorie angelegt/umbenannt/gelöscht, Regel angelegt mit
Prioritäts-Kollisionswarnung, Einzel-Regel-Vorschau und volle Simulation gegen echte Umsätze
getestet, Schutz der Spezial-Kategorien sowie der `Admin__Password`-Zugriffsschutz (503 ohne
Passwort, 401 mit falschem Passwort) bestätigt. 6 neue Unit-Tests für `AdminAuth`.

## 0.15.0 – Asset-Allokation nach Anlageklasse (Issue #4)

`grafana/dashboards/depot.json` zeigte die Asset-Allokation bisher nur je Einzelposition. Klärung
der offenen Frage aus `docs/konzept.md` Abschnitt 6 Phase 2 ("liefert comdirect eine brauchbare
Anlageklassen-Zuordnung?"): **ja** – `instrument.staticData.instrumentType`
(SHARE/BONDS/SUBSCRIPTION_RIGHT/ETF/PROFIT_PART_CERTIFICATE/FUND/WARRANT/CERTIFICATE/
NOT_AVAILABLE), bereits mit dem ohnehin für ISIN/Name genutzten `with-attr=instrument`
mitgeliefert – keine zusätzliche API-Anfrage nötig.

- Neue Spalte `portfolio_positions.instrument_type`
  (`db/migrations/0009_portfolio_positions_instrument_type.sql`) – NULL für vor dieser Version
  gespeicherte Positionen.
- `ComdirectFetch.Api.Instrument`/neues `StaticData` erweitert, `PortfolioFetchService` befüllt
  das neue Feld beim Speichern.
- `grafana/dashboards/depot.json`: neues Kreisdiagramm "Asset-Allokation nach Anlageklasse"
  (deutsche Labels per SQL-`CASE`, z. B. "Aktie", "ETF", "Fonds", "Zertifikat") – bestehendes
  Einzelpositions-Kreisdiagramm bleibt zusätzlich erhalten, beide nebeneinander; die Positionen-
  Tabelle zeigt die Anlageklasse jetzt ebenfalls als Spalte.
- 2 neue Unit-Tests für die neue JSON-Struktur (`Instrument.StaticData.InstrumentType`).

Live verifiziert gegen die echte comdirect-API und das echte Depot: alle 21 Positionen korrekt
klassifiziert (11 Aktien, 4 Zertifikate, 3 ETF, 3 Fonds, keine `NOT_AVAILABLE`/NULL), neues
Dashboard-Panel per Grafana-`/api/ds/query` direkt gegen die echte DB getestet – korrekte Summen
je Anlageklasse (z. B. Aktien 15.068,37 €).

## 0.14.0 – Aktive Benachrichtigung bei erforderlicher TAN-Freigabe (Issue #6)

Bisher war eine abgelaufene comdirect-Session (Status "Freigabe erforderlich") nur passiv über
`comdirectctl.sh status`/`sync_log` sichtbar – man bemerkte das Problem erst beim zufälligen
Nachschauen oder wenn in Grafana Datenlücken auffielen. Neue, komplett opt-in nutzbare
Benachrichtigung über zwei unabhängige Kanäle, **beide gleichzeitig aktivierbar** ("auch
parallel", wie gefordert):

- **E-Mail** (`Notification__EmailSmtpHost/Port/User/Password/UseStartTls/From/To`) über
  `System.Net.Mail.SmtpClient` (BCL, keine neue Paketabhängigkeit für diesen gelegentlichen,
  niedrigvolumigen Anwendungsfall).
- **Webhook** (`Notification__WebhookUrl`) – POST mit generischem JSON-Body, bewusst kein
  dienstspezifisches Schema (funktioniert z. B. mit ntfy.sh, Home Assistant, n8n/Node-RED).

Neues `ComdirectFetch.Worker.Services.NotificationService` spricht alle aktivierten Kanäle
**gleichzeitig** an (`Task.WhenAll`, nicht nacheinander) – ein langsamer oder fehlschlagender
Kanal blockiert die anderen nicht, ein Kanal-Fehler wird geloggt, aber nie an den Aufrufer
durchgereicht. Ausgelöst in `ComdirectAuthCoordinator.RefreshAsync`, genau am Übergang nach
`AuthState.NichtAuthentifiziert` (sowohl beim periodischen Token-Refresh als auch beim
Wiederherstellungsversuch nach einem Neustart) – feuert dadurch genau einmal pro Ausfall, nicht
bei jedem weiteren Intervall-Tick. Neuer `POST /debug/notify-test` /
`comdirectctl.sh notify-test` zum gefahrlosen Testen der Konfiguration, ohne eine echte
Session-Störung abwarten zu müssen.

3 neue Unit-Tests für die Dispatch-Logik (nur aktivierte Kanäle ansprechen, alle parallel
ansprechen trotz einzelnem Fehler, No-op ohne Konfiguration). Live verifiziert gegen echte
Test-Endpunkte (ein selbstgebauter SMTP- und Webhook-Empfänger): beide Kanäle gleichzeitig
konfiguriert, `POST /debug/notify-test` ausgelöst, E-Mail korrekt zugestellt (Betreff/Text mit
Umlauten intakt) und Webhook-JSON-Payload korrekt empfangen – ohne bestehende Session/echte
Zugangsdaten zu berühren.

## 0.13.1 – Umlaut-/Encoding-Bug in geloggten comdirect-Fehlertexten behoben (Issue #8)

Fehlertexte von comdirect (z. B. bei HTTP 429 "Die erlaubte Anzahl der Anfragen ist
überschritten") erschienen in Logs teils mit kaputten Zeichen (`�berschritten` statt
`überschritten`). Root Cause durch Reproduktion bestätigt, nicht geraten: comdirects
Fehler-Response-Bytes sind tatsächlich Latin-1/ISO-8859-1-kodiert, ohne verlässlichen
Charset-Header. .NETs Standard-`ReadAsStringAsync` geht ohne Charset-Header von UTF-8 aus und
ersetzt ungültige Byte-Folgen lautlos durch U+FFFD (`�`), statt einen Fehler zu melden oder eine
andere Kodierung zu versuchen – mit echten Latin-1-Bytes für "überschritten" durch .NETs
tatsächlichen Decoder reproduziert und exakt das `�berschritten`-Symptom aus dem Issue
nachgestellt.

Fix: neue, reine `ComdirectFetch.Domain.TextDecoding.DecodeUtf8WithLatin1Fallback` (unit-getestet)
versucht zuerst strikte UTF-8-Dekodierung und fällt nur bei einem tatsächlichen Dekodierfehler auf
Latin-1 zurück – echte UTF-8-Antworten sind davon unberührt. Eingesetzt in
`HttpResponseExtensions.EnsureSuccessWithBodyAsync` (Api) anstelle der Standard-Charset-Erkennung
von `ReadAsStringAsync`.

## 0.13.0 – Konsolidierungs- und Aufräumprozess für Zeitreihen-Daten (docs/konzept.md Abschnitt 11)

`account_balances` und `portfolio_snapshots`/`portfolio_positions` wuchsen mit jedem
Abrufintervall unbegrenzt (bei Standard-Intervallen ~288 Salden-Zeilen/Tag bei 3 Konten, ~500–700
Positions-Zeilen/Tag), `sync_log` ähnlich mit jedem Abrufversuch – kein Aufräum-Mechanismus
existierte bisher. Neuer `ComdirectFetch.Worker.Services.RetentionService`
(`BackgroundService`, täglich, auch manuell über `POST /debug/consolidate` bzw.
`comdirectctl.sh consolidate` anstoßbar):

- **Konsolidierung**: `account_balances`/`portfolio_snapshots` werden nach einer konfigurierbaren
  Rohdaten-Frist (`Retention__RawDataRetentionDays`) auf eine Zeile pro Tag reduziert – behalten
  wird der zeitlich letzte Wert des Tages (`ComdirectFetch.Data.RetentionRepository`, `DELETE …
  WHERE id <> MAX(id) je (account_id|portfolio_id, Tag)`). `portfolio_positions` der wegfallenden
  Snapshots werden im selben Zug mitgelöscht (Fremdschlüssel).
- **Optionale Löschung**: zusätzlich `Retention__ConsolidatedDataRetentionDays` gesetzt, werden
  bereits konsolidierte Zeilen nach einer weiteren Frist komplett gelöscht (Gesamtalter =
  Rohdaten-Frist + diese Frist).
- **`sync_log`**: separate, einfachere Politik ohne Konsolidierung –
  `Retention__SyncLogRetentionDays` löscht alte Zeilen direkt.
- **`transactions` bleibt bewusst unangetastet** – Finanz-Ledger, nie konsolidieren/löschen.
- **Komplett opt-in**: ohne gesetzte `Retention__*`-Zeiträume tut der Dienst nichts (kein
  `sync_log`-Eintrag, kein Rauschen) – heutiges Verhalten bleibt für bestehende Deployments exakt
  erhalten, analog zu `TokenEncryptionKeyBase64`/`CredentialKeyFilePath`.
- Jeder Lauf wird als `sync_log`-Eintrag protokolliert (neuer `data_kind`-Wert `Konsolidierung`,
  `db/migrations/0008_sync_log_add_konsolidierung.sql`), inkl. Zusammenfassung der
  konsolidierten/gelöschten Zeilenzahlen im Feld `error_message` (auch bei Erfolg) – damit über
  `GET /debug/summary`/`comdirectctl.sh status` nachvollziehbar, ohne direkten DB-Zugriff.

Live verifiziert: mit aggressiv kurzen Testwerten (`RawDataRetentionDays=0`) gegen die echte DB
laufen lassen, Konsolidierung auf 1 Zeile/Tag je Konto/Depot bestätigt, `sync_log`-Eintrag mit
korrekter Zusammenfassung geprüft, `transactions` unverändert.

## 0.12.0 – Sichere Ablage der comdirect-Zugangsdaten (docs/konzept.md Abschnitt 10)

Client-ID, Client-Secret, Zugangsnummer und PIN lagen bisher ausschließlich als Klartext in
`.env` und wurden unverändert als Container-Umgebungsvariablen übergeben – angreifbar über
`docker inspect`, `docker exec … env`, `/proc/<pid>/environ` oder versehentliche Env-Var-Dumps
in Logs. Umgesetzt nach dem in Abschnitt 10 dokumentierten Konzept, mit unterschiedlich starkem
Schutz je nach Sensibilität:

- **Client-ID/Client-Secret**: kommen jetzt primär über Docker-Compose-Secrets
  (`docker/docker-compose.yml`, Dateien unter `secrets/`, gitignored) statt über `env_file` –
  gelesen über den Standard-`Microsoft.Extensions.Configuration.KeyPerFile`-Provider
  (`Program.cs`, `/run/secrets`), kein Eigenbau. `.env` bleibt als Fallback für lokale
  Entwicklung ohne Docker Compose bestehen.
- **Zugangsnummer/PIN**: neuer Bootstrap-und-Wipe-Flow. `POST /admin/credentials` (bzw.
  `comdirectctl.sh set-credentials`, PIN interaktiv per `read -s` abgefragt, nie als
  Kommandozeilenargument) verschlüsselt sie AES-256-GCM (wiederverwendete
  `ComdirectFetch.Domain.SecretEncryption`) mit einem **neuen, dedizierten** Schlüssel – bewusst
  nicht `Comdirect__TokenEncryptionKeyBase64` – und legt sie in der neuen Tabelle
  `credential_store` ab (`db/migrations/0007_credential_store.sql`, neues
  `ComdirectFetch.Data.CredentialRepository`, analog zu `AuthTokenRepository`). Der Schlüssel
  selbst liegt in einer separaten, eng berechtigten Datei außerhalb von `.env` und außerhalb des
  Compose-Projektverzeichnisses (`Comdirect__CredentialKeyFilePath`, standardmäßig
  `/run/secrets/credential_key`, per Bind-Mount statt Compose-Secret bereitgestellt) – damit der
  Master-Schlüssel nicht dieselbe Exposition wie das `.env`-Klartext-Problem hat, das er gerade
  lösen soll. Nach erfolgreichem Bootstrap (mit Round-Trip-Verifikation, damit kein Zugriff
  stillschweigend verloren geht) können `Comdirect__Username`/`Comdirect__Password` aus `.env`
  entfernt werden. Neues `ComdirectFetch.Worker.Services.CredentialProvider` liefert die Werte
  zur Laufzeit statt beim DI-Container-Bau (über die neue `ComdirectFetch.Api.ICredentialProvider`-
  Abstraktion, die `ComdirectAuthClient` jetzt statt eines direkten `ComdirectApiOptions`-Zugriffs
  nutzt) und fällt ohne Bootstrap/ohne konfigurierten Schlüssel transparent auf `.env` zurück –
  komplett opt-in, keine erzwungene Migration bestehender Deployments.

Bewusst zu unterscheiden von der bereits umgesetzten Session-Token-Persistierung (0.11.0,
Issue #5): dort geht es um den Access-/Refresh-Token, hier um die vier Login-Zugangsdaten
selbst. Live verifiziert: Client-ID/Client-Secret über Docker-Secret-Dateien geladen,
Zugangsnummer/PIN per Bootstrap in `credential_store` verschlüsselt, aus `.env` entfernt,
Container neu gestartet, anschließender `/auth/start`-Lauf mit echter TAN-Freigabe erfolgreich
ohne dass Zugangsnummer/PIN noch in `.env` standen.

## 0.11.0 – Auth-Status übersteht jetzt einen Neustart (Issue #5)

`ComdirectAuthCoordinator` hielt den Session-Token bisher ausschließlich im Prozessspeicher –
jeder Container-Neustart brauchte eine komplett neue TAN-Freigabe, obwohl der bestehende
Refresh-Token oft noch gültig gewesen wäre. Neue Migration `0006_auth_token_store.sql` legt
eine Einzelzeilen-Tabelle für den verschlüsselten Token an; neue, reine
`ComdirectFetch.Domain.SecretEncryption` (AES-256-GCM, ohne DB-/API-Abhängigkeit) verschlüsselt
ihn, `ComdirectFetch.Data.AuthTokenRepository` speichert nur den opaken Blob (nonce/ciphertext/
tag) – kennt weder OAuthToken noch Kryptografie, passend zur Architekturregel, dass `Api` und
`Data` sich nicht gegenseitig referenzieren dürfen. `ComdirectAuthCoordinator` persistiert den
aktuellen Token nach jeder erfolgreichen TAN-Freigabe und jedem erfolgreichen Refresh und
versucht beim Start (`TryRestoreAsync`, aufgerufen in `Program.cs` vor `app.Run()`), einen
gespeicherten Token zu laden, zu entschlüsseln und per Refresh zu validieren – gelingt das,
ist die Session sofort `Authentifiziert`, ganz ohne `/auth/start`.

**Bewusst opt-in**: neue, optionale Umgebungsvariable `Comdirect__TokenEncryptionKeyBase64`
(Base64-kodierter 32-Byte-AES-256-Schlüssel, z. B. via `openssl rand -base64 32`). Ist sie
nicht gesetzt, bleibt das Verhalten exakt wie vorher (In-Memory-only, jeder Neustart braucht
eine neue TAN-Freigabe) – die neue Tabelle bleibt dann ungenutzt. Der Schlüssel selbst landet
nie in der Datenbank, nur der damit verschlüsselte Blob. Schlägt Entschlüsselung oder der
anschließende Refresh fehl (z. B. weil der Refresh-Token zwischenzeitlich abgelaufen ist),
wird der gespeicherte Token verworfen und der Dienst verhält sich wie ohne Persistierung –
eine neue TAN-Freigabe ist dann fällig, genau wie vorher.

Live verifiziert: echten Token nach TAN-Freigabe persistiert, Container neu gestartet,
`GET /health` zeigte danach direkt `Authentifiziert` ohne erneuten `/auth/start`-Aufruf.
Neue Tests in `SecretEncryptionTests.cs` (Roundtrip, unterschiedliche Nonces je Aufruf,
Erkennung von falschem Schlüssel/manipuliertem Ciphertext, Schlüssellängen-Validierung).

## 0.10.0 – Phase 4 der Auswertungen: Depot-Performance (vereinfacht)

Neues Grafana-Dashboard `grafana/dashboards/depot-performance.json` (KONZEPT.md Abschnitt 6,
Phase 4): Depotwert vs. Kapitaleinsatz über die Zeit, unrealisierter Gewinn/Verlust absolut
und in Prozent, jeweils als Zeitreihe plus aktueller Stand. Nutzt `total_value` und
`acquisition_value` aus `portfolio_snapshots` (bereits seit Phase 1 vorhanden).

**Bewusst vereinfachter Umfang** (Nutzerentscheidung): Eine vollständige, um externe Ein-/
Auszahlungen bereinigte Performance-Kennzahl bräuchte sowohl eine Depot↔Verrechnungskonto-
Verknüpfung (existiert im Datenmodell noch nicht) als auch echte Wertpapier-Kauf/Verkauf-
Umsätze zum Verifizieren (die Kategorie „Ordergebühren" hat in den Live-Daten aktuell 0
Treffer). Statt das ungetestet zu bauen, zeigt dieses Dashboard den unrealisierten Gewinn/
Verlust der aktuell gehaltenen Positionen (`total_value − acquisition_value`) – enthält
**keine** realisierten Gewinne aus bereits verkauften Positionen und **keine** externen
Ein-/Auszahlungen. Mit echten Daten verifiziert (5 Snapshots, aktuell −957,45 € / −2,68 %
unrealisiert). Volle Cashflow-Bereinigung als Folge-Issue vorgemerkt.

## 0.9.0 – Phase 3 der Auswertungen: Cashflow & Kostenübersicht

Neues Grafana-Dashboard `grafana/dashboards/cashflow.json` (KONZEPT.md Abschnitt 6, Phase 3):
Einnahmen/Ausgaben/Netto je Monat (Balkendiagramm), Ausgaben nach Kategorie (Kreisdiagramm +
Tabelle) sowie eine Gebührenübersicht (Kontoführungs-/Ordergebühren je Monat plus
Gesamtsumme im Zeitraum). Interne Umbuchungen zwischen eigenen Konten sind ausgeschlossen
(`category.type = InternNeutral`). Mit echten Daten verifiziert (7 Monate, z. B.
Einnahmen/Ausgaben im Bereich 300–9.500 €). Dabei einen Grafana-MySQL-Fallstrick gefunden:
`DATE_FORMAT(...)` zur Monats-Gruppierung liefert einen String, keinen echten Datumstyp –
Grafana kann daraus kein Zeitfeld ableiten ("unable to convert data to a time field").
Behoben mit `CAST(DATE_FORMAT(...) AS DATE)`, das den String wieder in ein echtes `DATE`
wandelt.

## 0.8.1 – Zeilenumbruch-Fallstrick bei zwei Regeln aus 0.8.0 behoben

Nach dem Deploy von 0.8.0 lief der Dienst länger authentifiziert weiter und sammelte 429
echte Kontoumsätze (statt der ursprünglichen 16). Die Neu-Kategorisierung (`/debug/recategorize`)
gegen diesen größeren Bestand deckte auf: Die Muster "Humble Bundle" und "Headline - Noth,
Schneider" trafen nie. Ursache: comdirect bricht `remittanceInfo` alle 35 Zeichen um und fügt
die zweistellige Zeilennummer **ohne Leerzeichen mitten ins Wort** ein (`Humble B02undle`,
`Headline02 - Noth, Schneider` bzw. im zweiten Vorkommen `Headline - Noth, Sch04neider`) – der
Mehrwort-Ausdruck steht dadurch nie zusammenhängend im Text. Behoben durch
`db/migrations/0005_fix_paypal_merchant_patterns.sql`: kürzere Ein-Wort-Muster ("Humble",
"Headline"), die in beiden Vorkommen unzerteilt bleiben. 0004 selbst bleibt unverändert
(append-only) – 0005 korrigiert die betroffenen Regeln per `UPDATE`. Tests entsprechend auf
den echten, umgebrochenen Text umgestellt statt einer idealisierten Fassung.

Bei größerer Kontrollstichprobe (429 statt 16 Umsätze) liegt die Fallback-Quote jetzt bei
71 % (304/429 "Sonstige Ausgabe") statt der ursprünglich beobachteten 81 % – die neuen Regeln
tragen bei signifikantem Datenvolumen (z. B. 30 Gaming/Unterhaltung-, 22 Lebensmittel-Treffer).
Weitere Muster für die verbleibenden Fallback-Umsätze sind nicht Teil dieser Änderung.

## 0.8.0 – Kategorisierungsregeln anhand echter Umsatzdaten erweitert, Neu-Kategorisierung

- Erste 16 echte Kontoumsätze ausgewertet: 13/16 (81 %) landeten mangels passender Regel im
  Vorzeichen-Fallback ("Sonstige Ausgabe"), eine Zinsgutschrift wurde fälschlich als
  "Sonstige Einnahme" statt "Zinsen/Dividenden" eingeordnet (dafür existierte bisher gar
  keine Regel). Behoben durch `db/migrations/0004_extend_categorization_rules.sql`: 7 neue,
  spezifischere Kategorien (`Gaming/Unterhaltung`, `Restaurants/Cafés`, `Telekommunikation`,
  `Altersvorsorge`, `Öffentlicher Nahverkehr`, `Apotheke/Gesundheit`, `Mode/Bekleidung`) plus
  14 neue Freitext-Regeln für die erkannten Händler/Muster sowie die fehlende
  Zinsen/Dividenden-Regel. Eine Buchung (generische Rechnungsformulierung ohne erkennbaren
  Händlernamen) bleibt bewusst unbehandelt und im Fallback, statt zu raten.
- PayPal-geroutete Buchungen (Humble Bundle, CinemaxX, Headline) matchen bewusst den
  Händlernamen im Text, nicht das gemeinsame PayPal-Routing-Präfix – das ist PayPals eigene
  ID, nicht händlerspezifisch, und hätte künftige fremde PayPal-Zahlungen fälschlich in
  dieselbe Kategorie gesteckt. Gleiches Prinzip bei `SumUp` (generischer Terminal-Anbieter):
  Muster ist der konkrete Café-Name, nicht der SumUp-Präfix.
- Neuer, generischer Neu-Kategorisierungs-Mechanismus (KONZEPT.md Abschnitt 6/9, bisher
  offener Punkt): `TransactionRepository.GetAllNonManuallyCategorizedAsync` liefert alle
  nicht manuell kategorisierten Umsätze unabhängig vom aktuellen `category_id`-Wert (statt
  nur `category_id IS NULL` wie `GetUncategorizedAsync`); `CategorizationService.RecategorizeAllAsync`
  wendet den aktuellen Regelsatz erneut darauf an. Erreichbar über `POST /debug/recategorize`
  und `comdirectctl.sh recategorize`, wiederverwendbar für jede künftige Regeländerung, nicht
  nur den aktuellen Bestand.

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
