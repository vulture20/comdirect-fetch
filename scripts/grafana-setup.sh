#!/usr/bin/env bash
# grafana-setup.sh – legt die MariaDB-Datenquelle (uid comdirect-mariadb) und alle
# Grafana-Dashboards (grafana/dashboards/*.json) per Grafana-HTTP-API an bzw. aktualisiert sie
# (siehe README.md "Grafana-Dashboards", CLAUDE.md, GitHub-Issue #9). Läuft auf dem Host, nicht
# im Container. Ersetzt den bisherigen manuellen/Ad-hoc-API-Call-Workflow für Erstinbetriebnahme
# und Rebuilds der Grafana-Instanz.
#
# Voraussetzungen: curl, jq
# GRAFANA_URL per Umgebungsvariable überschreibbar (Standard: http://localhost:3000).
# GRAFANA_TOKEN (Grafana Service-Account-Token MIT ADMIN-ROLLE) ist erforderlich - eine
# Editor-Rolle darf keine Datenquellen anlegen (siehe CLAUDE.md). Datenbank-Verbindungsdaten für
# die Datenquelle werden aus der lokalen .env dieses Projekts gelesen (Database__Host/Port/Name/
# User/Password) - dieselbe DB, in die der Fetch-Dienst schreibt.
#
# Idempotent: ein erneuter Lauf aktualisiert eine bereits vorhandene Datenquelle/Dashboards
# (overwrite), statt sie zu duplizieren.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
ENV_FILE="${REPO_ROOT}/.env"
DASHBOARDS_DIR="${REPO_ROOT}/grafana/dashboards"

GRAFANA_URL="${GRAFANA_URL:-http://localhost:3000}"
SCRIPT_NAME="$(basename "$0")"
DATASOURCE_UID="comdirect-mariadb"
DATASOURCE_NAME="comdirect-fetch MariaDB"

usage() {
  cat <<EOF
grafana-setup.sh – Grafana-Datenquelle + Dashboards per API anlegen/aktualisieren

Verwendung:
  ${SCRIPT_NAME} all           Datenquelle UND alle Dashboards anlegen/aktualisieren (Standard)
  ${SCRIPT_NAME} datasource    Nur die MariaDB-Datenquelle (uid ${DATASOURCE_UID})
  ${SCRIPT_NAME} dashboards    Nur die Dashboards aus grafana/dashboards/*.json
  ${SCRIPT_NAME} help          Diese Hilfe anzeigen

Voraussetzungen:
  - curl, jq
  - Umgebungsvariable GRAFANA_TOKEN: Grafana Service-Account-Token MIT ADMIN-ROLLE
    (eine Editor-Rolle darf keine Datenquellen anlegen - siehe CLAUDE.md)
  - Umgebungsvariable GRAFANA_URL überschreibt die Basis-URL (Standard: ${GRAFANA_URL})
  - Datenbank-Verbindungsdaten für die Datenquelle werden aus ${ENV_FILE} gelesen
    (Database__Host/Port/Name/User/Password)

Idempotent: bereits vorhandene Datenquelle/Dashboards werden aktualisiert, nicht dupliziert.
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

require_token() {
  if [[ -z "${GRAFANA_TOKEN:-}" ]]; then
    echo "Fehler: GRAFANA_TOKEN nicht gesetzt (Grafana Service-Account-Token mit Admin-Rolle)." >&2
    exit 64
  fi
}

# Liest nur die benötigten Database__*-Zeilen aus .env, statt die komplette Datei zu sourcen -
# die enthält auch comdirect-Zugangsdaten, die dieses Skript nicht braucht.
load_db_env() {
  if [[ ! -f "$ENV_FILE" ]]; then
    echo "Fehler: ${ENV_FILE} nicht gefunden - für die Datenquelle werden Database__*-Werte benötigt." >&2
    exit 64
  fi

  DB_HOST="$(grep -E '^Database__Host=' "$ENV_FILE" | tail -n1 | cut -d= -f2-)"
  DB_PORT="$(grep -E '^Database__Port=' "$ENV_FILE" | tail -n1 | cut -d= -f2-)"
  DB_NAME="$(grep -E '^Database__Name=' "$ENV_FILE" | tail -n1 | cut -d= -f2-)"
  DB_USER="$(grep -E '^Database__User=' "$ENV_FILE" | tail -n1 | cut -d= -f2-)"
  DB_PASSWORD="$(grep -E '^Database__Password=' "$ENV_FILE" | tail -n1 | cut -d= -f2-)"

  if [[ -z "$DB_HOST" || -z "$DB_PORT" || -z "$DB_NAME" || -z "$DB_USER" ]]; then
    echo "Fehler: Database__Host/Port/Name/User in ${ENV_FILE} unvollständig." >&2
    exit 64
  fi
}

# Führt einen Grafana-API-Request aus und setzt GRAFANA_HTTP_CODE/GRAFANA_HTTP_BODY.
grafana_request() {
  local method="$1" path="$2" body="${3:-}"
  local tmp_file
  tmp_file="$(mktemp)"
  local curl_args=(-s -o "$tmp_file" -w '%{http_code}' -X "$method" "${GRAFANA_URL}${path}" \
    -H "Authorization: Bearer ${GRAFANA_TOKEN}" -H 'Accept: application/json')
  [[ -n "$body" ]] && curl_args+=(-H 'Content-Type: application/json' --data "$body")

  if ! GRAFANA_HTTP_CODE="$(curl "${curl_args[@]}")"; then
    echo "Fehler: Grafana unter ${GRAFANA_URL} nicht erreichbar." >&2
    rm -f "$tmp_file"
    exit 2
  fi
  GRAFANA_HTTP_BODY="$(cat "$tmp_file")"
  rm -f "$tmp_file"
}

cmd_datasource() {
  require_token
  load_db_env

  local payload
  payload="$(jq -n \
    --arg uid "$DATASOURCE_UID" \
    --arg name "$DATASOURCE_NAME" \
    --arg url "${DB_HOST}:${DB_PORT}" \
    --arg database "$DB_NAME" \
    --arg user "$DB_USER" \
    --arg password "$DB_PASSWORD" \
    '{uid: $uid, name: $name, type: "mysql", access: "proxy", url: $url, database: $database,
      user: $user, isDefault: false, basicAuth: false, jsonData: {},
      secureJsonData: {password: $password}}')"

  # uid-basierter GET: 200 = Datenquelle existiert bereits -> PUT (Update), 404 = neu -> POST
  # (Create). Idempotent, kein Duplikat bei erneutem Lauf.
  grafana_request GET "/api/datasources/uid/${DATASOURCE_UID}"
  if [[ "$GRAFANA_HTTP_CODE" == "200" ]]; then
    grafana_request PUT "/api/datasources/uid/${DATASOURCE_UID}" "$payload"
  else
    grafana_request POST "/api/datasources" "$payload"
  fi

  if [[ "$GRAFANA_HTTP_CODE" != "200" ]]; then
    echo "Fehler beim Anlegen/Aktualisieren der Datenquelle (HTTP ${GRAFANA_HTTP_CODE}): ${GRAFANA_HTTP_BODY}" >&2
    exit 1
  fi
  echo "Datenquelle '${DATASOURCE_UID}' angelegt/aktualisiert (${DB_HOST}:${DB_PORT}/${DB_NAME})."
}

cmd_dashboards() {
  require_token

  if [[ ! -d "$DASHBOARDS_DIR" ]]; then
    echo "Fehler: ${DASHBOARDS_DIR} nicht gefunden." >&2
    exit 64
  fi

  shopt -s nullglob
  local files=("$DASHBOARDS_DIR"/*.json)
  shopt -u nullglob
  if [[ ${#files[@]} -eq 0 ]]; then
    echo "Fehler: keine Dashboard-JSON-Dateien in ${DASHBOARDS_DIR} gefunden." >&2
    exit 64
  fi

  local file dashboard_json payload title
  for file in "${files[@]}"; do
    dashboard_json="$(cat "$file")"
    payload="$(jq -n --argjson dashboard "$dashboard_json" '{dashboard: $dashboard, overwrite: true}')"

    grafana_request POST "/api/dashboards/db" "$payload"
    if [[ "$GRAFANA_HTTP_CODE" != "200" ]]; then
      echo "Fehler beim Importieren von $(basename "$file") (HTTP ${GRAFANA_HTTP_CODE}): ${GRAFANA_HTTP_BODY}" >&2
      exit 1
    fi
    title="$(echo "$dashboard_json" | jq -r '.title')"
    echo "Dashboard '${title}' ($(basename "$file")) angelegt/aktualisiert."
  done
}

require_deps

case "${1:-all}" in
  all) cmd_datasource; cmd_dashboards ;;
  datasource) cmd_datasource ;;
  dashboards) cmd_dashboards ;;
  help|-h|--help) usage ;;
  *) echo "Unbekannter Befehl: ${1}. Siehe '${SCRIPT_NAME} help'." >&2; exit 64 ;;
esac
