#!/usr/bin/env bash
# Dumps Recall's database to /var/backups/recall, reading settings from .env.prod.
set -euo pipefail
umask 077

SCRIPT_DIR="$(cd "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")" && pwd)"
ENV_FILE="${ENV_FILE:-$SCRIPT_DIR/.env.prod}"
BACKUP_DIR="/var/backups/recall"
KEEP_DAYS=9

# Reads one value from .env.prod (last occurrence wins, surrounding quotes removed).
env_value() {
  local value
  value=$(grep -E "^${1}=" "$ENV_FILE" | tail -n 1 | cut -d= -f2-) || true
  value="${value%\"}"; value="${value#\"}"
  if [[ -z "$value" ]]; then
    echo "ERROR: $1 is not set in $ENV_FILE" >&2
    exit 1
  fi
  printf '%s' "$value"
}

CONTAINER="$(env_value POSTGRES_HOST)"   # the Postgres container's name
DB_NAME="$(env_value POSTGRES_DB)"
DB_USER="$(env_value POSTGRES_USER)"
PGPASSWORD="$(env_value POSTGRES_PASSWORD)"
export PGPASSWORD

mkdir -p "$BACKUP_DIR"
OUTPUT="$BACKUP_DIR/${DB_NAME}_$(date -u +%Y-%m-%d_%H-%M-%S).sql.gz"
PARTIAL="$OUTPUT.partial"
trap 'rm -f "$PARTIAL"' EXIT

echo "Dumping '$DB_NAME' from container '$CONTAINER'..."
docker exec -e PGPASSWORD "$CONTAINER" \
  pg_dump -h 127.0.0.1 -p 5432 -U "$DB_USER" --no-password "$DB_NAME" \
  | gzip > "$PARTIAL"

gzip -t "$PARTIAL"
mv "$PARTIAL" "$OUTPUT"
trap - EXIT

echo "Removing dumps older than $KEEP_DAYS days"
find "$BACKUP_DIR" -type f -name '*.sql.gz' -mtime +"$KEEP_DAYS" -delete

echo "Backup complete: $OUTPUT ($(du -h "$OUTPUT" | cut -f1))"
