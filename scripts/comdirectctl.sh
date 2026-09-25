#!/usr/bin/env bash
# comdirectctl.sh – Hilfsskript für TAN-Freigabe und Status-Abfrage des comdirect-fetch
# Diensts (siehe README.md, docs/konzept.md Abschnitt 3). Läuft auf dem Host, nicht im
# Container, und spricht den Dienst über seine HTTP-API an.
#
# Voraussetzungen: curl, jq
# Basis-URL per Umgebungsvariable COMDIRECT_FETCH_URL überschreibbar
# (Standard: http://localhost:8750, siehe docker/docker-compose.yml).

set -euo pipefail

BASE_URL="${COMDIRECT_FETCH_URL:-http://localhost:8750}"
SCRIPT_NAME="$(basename "$0")"
JSON_OUTPUT=0

TMP_FILE=""
# "if" statt "[[ ]] &&" verwenden: Unter 'set -e' würde ein zuletzt falsch ausgewertetes
# "&&" im EXIT-Trap sonst den eigentlichen Exit-Code des Skripts überschreiben.
cleanup() {
  if [[ -n "$TMP_FILE" && -f "$TMP_FILE" ]]; then
    rm -f "$TMP_FILE"
  fi
}
trap cleanup EXIT

usage() {
  cat <<EOF
comdirectctl.sh – Hilfsskript für den comdirect-fetch Dienst

Verwendung:
  ${SCRIPT_NAME} status [--json]
  ${SCRIPT_NAME} auth start
  ${SCRIPT_NAME} auth confirm [TAN_CODE]
  ${SCRIPT_NAME} fetch-now
  ${SCRIPT_NAME} recategorize
  ${SCRIPT_NAME} set-credentials
  ${SCRIPT_NAME} help

Befehle:
  status          Auth-Status, App-Version, Zeilenanzahl je Tabelle und die letzten
                  10 sync_log-Einträge abfragen. Ohne --json menschenlesbar formatiert,
                  mit --json als rohes JSON für Skripte/Monitoring.
                  Exit-Code: 0 = authentifiziert, 1 = keine/ausstehende Freigabe,
                  2 = Dienst nicht erreichbar oder Fehlerantwort.
  auth start      TAN-Freigabe auslösen (POST /auth/start), z. B. eine PushTAN-
                  Benachrichtigung in der comdirect-App.
  auth confirm    TAN-Freigabe bestätigen (POST /auth/confirm). TAN_CODE nur bei
                  photoTAN/mobileTAN nötig, bei PushTAN weglassen.
  fetch-now       Sofortigen Abruf aller Datenarten anstoßen (POST /debug/fetch-now),
                  ohne auf die konfigurierten Intervalle zu warten. Berührt keine
                  Session/TAN, gefahrlos wiederholbar.
  recategorize    Alle nicht manuell kategorisierten Umsätze mit dem aktuellen Regelsatz
                  neu einordnen (POST /debug/recategorize), z. B. nach einer Erweiterung
                  oder Korrektur der Kategorisierungsregeln. Berührt keine Session/TAN,
                  gefahrlos wiederholbar; manuelle Zuordnungen bleiben unverändert.
  set-credentials Bootstrap-Schritt (KONZEPT.md Abschnitt 10 B): fragt Zugangsnummer/PIN
                  interaktiv ab (PIN nicht auf dem Bildschirm sichtbar, nicht als
                  Kommandozeilenargument) und legt sie verschlüsselt in der DB ab
                  (POST /admin/credentials). Erfordert eine konfigurierte Schlüsseldatei
                  (Comdirect__CredentialKeyFilePath) auf dem Dienst. Danach können
                  Comdirect__Username/Comdirect__Password aus .env entfernt werden.
  help            Diese Hilfe anzeigen.

Umgebungsvariable COMDIRECT_FETCH_URL überschreibt die Basis-URL
(Standard: ${BASE_URL}).

ACHTUNG: comdirect sperrt nach drei falschen TAN-Eingaben oder fünf TAN-Challenges
ohne zwischenzeitliche Einlösung einer korrekten TAN den GESAMTEN Online-Banking-
Zugang, nicht nur den API-Zugriff. "auth start" daher nicht automatisiert oder in
einer Schleife aufrufen.
EOF
}

require_deps() {
  local missing=()
  for cmd in curl jq; do
    command -v "$cmd" >/dev/null 2>&1 || missing+=("$cmd")
  done
  if [[ ${#missing[@]} -gt 0 ]]; then
    echo "Fehler: benötigt ${missing[*]}, aber nicht installiert." >&2
    exit 3
  fi
}

# Führt einen API-Request aus und setzt HTTP_CODE/HTTP_BODY. Bricht bei Netzwerkfehlern
# (Dienst nicht erreichbar) sofort mit Exit-Code 2 ab.
http_request() {
  local method="$1" path="$2" body="${3:-}"
  TMP_FILE="$(mktemp)"
  local curl_args=(-s -o "$TMP_FILE" -w '%{http_code}' -X "$method" "${BASE_URL}${path}" -H 'Accept: application/json')
  [[ -n "$body" ]] && curl_args+=(-H 'Content-Type: application/json' --data "$body")

  if ! HTTP_CODE="$(curl "${curl_args[@]}")"; then
    echo "Fehler: comdirect-fetch unter ${BASE_URL} nicht erreichbar." >&2
    exit 2
  fi
  HTTP_BODY="$(cat "$TMP_FILE")"
  rm -f "$TMP_FILE"
  TMP_FILE=""
}

cmd_status() {
  http_request GET /health
  [[ "$HTTP_CODE" == "200" ]] || { echo "Fehler: /health antwortete mit HTTP ${HTTP_CODE}: ${HTTP_BODY}" >&2; exit 2; }
  local health="$HTTP_BODY"

  http_request GET /debug/summary
  [[ "$HTTP_CODE" == "200" ]] || { echo "Fehler: /debug/summary antwortete mit HTTP ${HTTP_CODE}: ${HTTP_BODY}" >&2; exit 2; }
  local summary="$HTTP_BODY"

  local combined
  combined="$(jq -n --argjson health "$health" --argjson summary "$summary" \
    '{version: $health.version, authState: $health.authState, counts: $summary.counts, recentSyncLog: $summary.recentSyncLog}')"

  if [[ "$JSON_OUTPUT" -eq 1 ]]; then
    echo "$combined"
  else
    print_human_status "$combined"
  fi

  [[ "$(echo "$combined" | jq -r '.authState')" == "Authentifiziert" ]] && exit 0 || exit 1
}

print_human_status() {
  local data="$1" auth_state
  auth_state="$(echo "$data" | jq -r '.authState')"

  echo "comdirect-fetch Status"
  echo "======================"
  echo "URL:          ${BASE_URL}"
  echo "Version:      $(echo "$data" | jq -r '.version')"
  if [[ "$auth_state" == "Authentifiziert" ]]; then
    echo "Auth-Status:  OK (${auth_state})"
  else
    echo "Auth-Status:  ACHTUNG (${auth_state}) – '${SCRIPT_NAME} auth start' nötig"
  fi
  echo
  echo "Zeilen je Tabelle:"
  echo "$data" | jq -r '.counts | to_entries[] | "  \(.key)\t\(.value)"' | column -t -s $'\t'
  echo
  echo "Letzte Abrufe (neueste zuerst):"
  echo "$data" | jq -r '.recentSyncLog[] | "  [\(.status)]\t\(.dataKind)\t\(.errorMessage // "")"' | column -t -s $'\t'
}

cmd_auth_start() {
  echo "ACHTUNG: comdirect sperrt nach 5 TAN-Challenges ohne Einlösung den gesamten"
  echo "Online-Banking-Zugang. Nur einmal aufrufen und die Freigabe wirklich abschließen."
  echo

  http_request POST /auth/start
  if [[ "$HTTP_CODE" != "200" ]]; then
    echo "Fehler (HTTP ${HTTP_CODE}): ${HTTP_BODY}" >&2
    exit 1
  fi

  local typ msg
  typ="$(echo "$HTTP_BODY" | jq -r '.typ')"
  msg="$(echo "$HTTP_BODY" | jq -r '.message')"
  echo "TAN-Typ: ${typ}"
  echo "${msg}"
  if [[ "$typ" == "P_TAN_PUSH" ]]; then
    echo "-> Freigabe in der comdirect-App bestätigen, danach: ${SCRIPT_NAME} auth confirm"
  else
    echo "-> TAN-Code ermitteln, danach: ${SCRIPT_NAME} auth confirm <TAN_CODE>"
  fi
}

cmd_auth_confirm() {
  local tan_code="${1:-}" payload
  if [[ -n "$tan_code" ]]; then
    payload="$(jq -n --arg tan "$tan_code" '{tanCode: $tan}')"
  else
    payload='{}'
  fi

  http_request POST /auth/confirm "$payload"
  if [[ "$HTTP_CODE" != "200" ]]; then
    echo "Fehler (HTTP ${HTTP_CODE}): ${HTTP_BODY}" >&2
    exit 1
  fi
  echo "$HTTP_BODY" | jq -r '.message'
}

cmd_fetch_now() {
  http_request POST /debug/fetch-now
  if [[ "$HTTP_CODE" != "200" ]]; then
    echo "Fehler (HTTP ${HTTP_CODE}): ${HTTP_BODY}" >&2
    exit 1
  fi
  echo "$HTTP_BODY" | jq -r '.message'
  echo "Details: ${SCRIPT_NAME} status"
}

cmd_recategorize() {
  http_request POST /debug/recategorize
  if [[ "$HTTP_CODE" != "200" ]]; then
    echo "Fehler (HTTP ${HTTP_CODE}): ${HTTP_BODY}" >&2
    exit 1
  fi
  echo "$HTTP_BODY" | jq -r '.message'
}

# Bootstrap-Schritt für Zugangsnummer/PIN (KONZEPT.md Abschnitt 10 B). PIN bewusst per "read -s"
# abgefragt statt als Argument, damit sie weder im Terminal sichtbar noch in der Shell-History/
# Prozessliste (ps) landet.
cmd_set_credentials() {
  echo "Setzt Zugangsnummer/PIN verschlüsselt in der DB ab (KONZEPT.md Abschnitt 10 B)."
  echo "Voraussetzung: Comdirect__CredentialKeyFilePath zeigt auf dem Dienst auf eine vorhandene Schlüsseldatei."
  echo

  local username password password_confirm payload
  read -r -p "Zugangsnummer: " username
  read -r -s -p "PIN: " password
  echo
  read -r -s -p "PIN (Wiederholung): " password_confirm
  echo

  if [[ -z "$username" || -z "$password" ]]; then
    echo "Fehler: Zugangsnummer und PIN dürfen nicht leer sein." >&2
    exit 64
  fi
  if [[ "$password" != "$password_confirm" ]]; then
    echo "Fehler: PIN-Eingaben stimmen nicht überein." >&2
    exit 64
  fi

  payload="$(jq -n --arg u "$username" --arg p "$password" '{username: $u, password: $p}')"
  http_request POST /admin/credentials "$payload"
  if [[ "$HTTP_CODE" != "200" ]]; then
    echo "Fehler (HTTP ${HTTP_CODE}): ${HTTP_BODY}" >&2
    exit 1
  fi
  echo "$HTTP_BODY" | jq -r '.message'
  echo
  echo "-> Jetzt Comdirect__Username/Comdirect__Password aus .env entfernen (siehe README.md)."
}

require_deps

case "${1:-help}" in
  status)
    shift || true
    [[ "${1:-}" == "--json" ]] && JSON_OUTPUT=1
    cmd_status
    ;;
  auth)
    shift || true
    case "${1:-}" in
      start) cmd_auth_start ;;
      confirm) shift || true; cmd_auth_confirm "${1:-}" ;;
      *) echo "Unbekannter Befehl 'auth ${1:-<leer>}'. Erwartet: 'auth start' oder 'auth confirm'. Siehe '${SCRIPT_NAME} help'." >&2; exit 64 ;;
    esac
    ;;
  fetch-now) cmd_fetch_now ;;
  recategorize) cmd_recategorize ;;
  set-credentials) cmd_set_credentials ;;
  help|-h|--help) usage ;;
  *) echo "Unbekannter Befehl: ${1}. Siehe '${SCRIPT_NAME} help'." >&2; exit 64 ;;
esac
