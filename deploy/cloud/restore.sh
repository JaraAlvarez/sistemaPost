#!/usr/bin/env bash
# Restaura un respaldo de backup.sh: REEMPLAZA la BD pos_cloud y las llaves de Data Protection.
#
# Uso (desde deploy/cloud):
#   ./restore.sh pos-cloud-AAAAMMDDTHHMMSSZ.tar.age --identity /ruta/clave-age.txt   # cifrado (clave privada de age)
#   ./restore.sh pos-cloud-AAAAMMDDTHHMMSSZ.tar                                        # ya descifrado en otro equipo
# Pasos: detiene la aplicación → asegura roles (db-init) → recrea la BD vacía → pg_restore → restaura las llaves →
# levanta todo (db-init vuelve a aplicar permisos y migraciones pendientes) → verifica la auditoría.
# ENV_FILE y POS_COMPOSE permiten usar otro .env u otro comando de compose. ASSUME_YES=1 omite la confirmación.
set -euo pipefail

cd "$(dirname "$0")"
archive="${1:?Uso: ./restore.sh <respaldo.tar.age|respaldo.tar> [--identity <clave-age>]}"
identity=""
if [[ "${2:-}" == "--identity" ]]; then identity="${3:?Falta la ruta de la clave age}"; fi
[[ "$archive" = /* ]] || archive="$OLDPWD/$archive"
[[ -z "$identity" || "$identity" = /* ]] || identity="$OLDPWD/$identity"
[[ -f "$archive" ]] || { echo "No existe $archive" >&2; exit 1; }

ENV_FILE="${ENV_FILE:-.env}"
[[ -f "$ENV_FILE" ]] || { echo "No existe $ENV_FILE" >&2; exit 1; }
set -a; # shellcheck disable=SC1090
. "./$ENV_FILE"; set +a
read -r -a COMPOSE <<< "${POS_COMPOSE:-docker compose --env-file $ENV_FILE}"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
umask 077

if [[ "$archive" == *.age ]]; then
    [[ -n "$identity" ]] || { echo "Para un archivo .age indique --identity <clave-age>" >&2; exit 1; }
    age -d -i "$identity" "$archive" | tar -C "$work" -xf -
else
    tar -C "$work" -xf "$archive"
fi
(cd "$work" && sha256sum -c SHA256SUMS)

if [[ "${ASSUME_YES:-0}" != "1" ]]; then
    read -r -p "Se REEMPLAZARÁ la base de datos pos_cloud de este servidor. Escriba RESTAURAR para continuar: " answer
    [[ "$answer" == "RESTAURAR" ]] || { echo "Cancelado."; exit 1; }
fi

echo "Deteniendo la aplicación…"
"${COMPOSE[@]}" stop app caddy || true
"${COMPOSE[@]}" up -d --wait postgres
echo "Asegurando roles (db-init)…"
"${COMPOSE[@]}" run --rm db-init

echo "Recreando la base de datos vacía…"
"${COMPOSE[@]}" exec -T postgres psql -v ON_ERROR_STOP=1 -U postgres -d postgres <<'SQL'
DROP DATABASE IF EXISTS pos_cloud WITH (FORCE);
CREATE DATABASE pos_cloud OWNER pos_owner ENCODING 'UTF8' LOCALE_PROVIDER builtin BUILTIN_LOCALE 'C.UTF-8' TEMPLATE template0;
SQL

echo "Restaurando el volcado…"
"${COMPOSE[@]}" exec -T postgres pg_restore -U postgres -d pos_cloud --exit-on-error < "$work/pos_cloud.dump"

echo "Restaurando las llaves de Data Protection…"
# Con el mismo usuario sin privilegios de la imagen (dueño del volumen): los archivos quedan con el dueño correcto.
keys_run=("${COMPOSE[@]}" run --rm --no-deps -T)
"${keys_run[@]}" --entrypoint find app /var/lib/pos-cloud/dataprotection -mindepth 1 -delete
"${keys_run[@]}" --entrypoint tar app -C /var/lib/pos-cloud --no-same-owner -xf - < "$work/dataprotection.tar"

echo "Levantando los servicios…"
"${COMPOSE[@]}" up -d --wait
"${COMPOSE[@]}" exec -T app dotnet /app/Pos.Cloud.Host.dll verify-audit
echo "Restauración terminada."
