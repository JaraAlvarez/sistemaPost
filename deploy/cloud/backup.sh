#!/usr/bin/env bash
# Respaldo cifrado del servidor de licencias (L-11): pg_dump de pos_cloud (rol pos_backup, solo lectura) + llaves de
# Data Protection (sin ellas no se descifran los secretos TOTP del portal), empaquetados y cifrados con age para un
# destinatario PÚBLICO: el VPS puede cifrar pero no descifrar. Luego se copia fuera del VPS (rclone o rsync).
#
# Uso (desde deploy/cloud):   ./backup.sh
# Cron diario (root):         15 3 * * * cd /opt/pos/deploy/cloud && ./backup.sh >> /var/log/pos-cloud-backup.log 2>&1
# Variables (de .env): BACKUP_AGE_RECIPIENT, BACKUP_DIR, BACKUP_RETENTION_DAYS, BACKUP_REMOTE_TOOL, BACKUP_REMOTE,
#                      DB_BACKUP_PASSWORD. ENV_FILE y POS_COMPOSE permiten usar otro .env u otro comando de compose.
# La clave privada de FIRMA no entra en este respaldo: se respalda una sola vez, cifrada, al generarla (ver la guía).
set -euo pipefail

cd "$(dirname "$0")"
ENV_FILE="${ENV_FILE:-.env}"
[[ -f "$ENV_FILE" ]] || { echo "No existe $ENV_FILE" >&2; exit 1; }
set -a; # shellcheck disable=SC1090
. "./$ENV_FILE"; set +a
read -r -a COMPOSE <<< "${POS_COMPOSE:-docker compose --env-file $ENV_FILE}"

: "${BACKUP_AGE_RECIPIENT:?Defina BACKUP_AGE_RECIPIENT (clave pública age1…) en $ENV_FILE}"
: "${DB_BACKUP_PASSWORD:?Defina DB_BACKUP_PASSWORD en $ENV_FILE}"
BACKUP_DIR="${BACKUP_DIR:-/var/backups/pos-cloud}"
BACKUP_RETENTION_DAYS="${BACKUP_RETENTION_DAYS:-14}"
command -v age >/dev/null || { echo "Falta age (sudo apt install age)" >&2; exit 1; }

stamp="$(date -u +%Y%m%dT%H%M%SZ)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
umask 077
mkdir -p "$BACKUP_DIR"

echo "[$(date -Is)] Respaldo $stamp: volcando la base de datos…"
"${COMPOSE[@]}" exec -T -e PGPASSWORD="$DB_BACKUP_PASSWORD" postgres \
    pg_dump -h 127.0.0.1 -U pos_backup -d pos_cloud --format=custom --compress=6 > "$work/pos_cloud.dump"
[[ -s "$work/pos_cloud.dump" ]] || { echo "El volcado quedó vacío" >&2; exit 1; }

echo "[$(date -Is)] Copiando las llaves de Data Protection…"
"${COMPOSE[@]}" exec -T app tar -C /var/lib/pos-cloud -cf - dataprotection > "$work/dataprotection.tar"

(cd "$work" && sha256sum pos_cloud.dump dataprotection.tar > SHA256SUMS)
out="$BACKUP_DIR/pos-cloud-$stamp.tar.age"
tar -C "$work" -cf - pos_cloud.dump dataprotection.tar SHA256SUMS | age -r "$BACKUP_AGE_RECIPIENT" -o "$out"
echo "[$(date -Is)] Respaldo cifrado: $out ($(du -h "$out" | cut -f1))"

if [[ -n "${BACKUP_REMOTE:-}" ]]; then
    case "${BACKUP_REMOTE_TOOL:-rclone}" in
        rclone) rclone copy "$out" "$BACKUP_REMOTE" ;;
        rsync)  rsync -a "$out" "$BACKUP_REMOTE/" ;;
        *) echo "BACKUP_REMOTE_TOOL desconocido: $BACKUP_REMOTE_TOOL" >&2; exit 1 ;;
    esac
    echo "[$(date -Is)] Copiado fuera del VPS: $BACKUP_REMOTE"
else
    echo "[$(date -Is)] AVISO: BACKUP_REMOTE vacío; el respaldo solo está en este servidor." >&2
fi

find "$BACKUP_DIR" -maxdepth 1 -name 'pos-cloud-*.tar.age' -mtime +"$BACKUP_RETENTION_DAYS" -print -delete
echo "[$(date -Is)] Listo."
