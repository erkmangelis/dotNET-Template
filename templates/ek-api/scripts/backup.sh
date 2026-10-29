#!/usr/bin/env bash
# Nightly PostgreSQL backup for the compose deployment. See docs/database.md.
#   crontab -e
#   15 3 * * * cd /srv/ek-app-slug && ./scripts/backup.sh >> backups/backup.log 2>&1
set -euo pipefail

cd "$(dirname "$0")/.."
KEEP_DAYS="${KEEP_DAYS:-14}"
mkdir -p backups

file="backups/app-$(date -u +%Y%m%dT%H%M%SZ).dump"
# Custom format: compressed, and restorable table by table with pg_restore.
docker compose exec -T db pg_dump --username app --dbname app --format custom > "$file.partial"
mv "$file.partial" "$file"
echo "$(date -u +%FT%TZ) wrote $file ($(du -h "$file" | cut -f1))"

find backups -name 'app-*.dump' -mtime +"$KEEP_DAYS" -delete
