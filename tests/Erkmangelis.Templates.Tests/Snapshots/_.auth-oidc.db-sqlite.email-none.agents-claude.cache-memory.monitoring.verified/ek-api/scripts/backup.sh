#!/usr/bin/env bash
# Nightly SQLite backup for the compose deployment. See docs/database.md.
#   crontab -e
#   15 3 * * * cd /srv/acme-notes && ./scripts/backup.sh >> backups/backup.log 2>&1
set -euo pipefail

cd "$(dirname "$0")/.."
KEEP_DAYS="${KEEP_DAYS:-14}"
mkdir -p backups

file="backups/app-$(date -u +%Y%m%dT%H%M%SZ).db"
# SQLite's online backup: a consistent copy while the API keeps writing.
docker run --rm -v acme-notes_app-data:/data -v "$PWD/backups:/backups" alpine:3.22 \
  sh -c "apk add --no-cache --quiet sqlite && sqlite3 /data/app.db \".backup '/backups/$(basename "$file").partial'\""
mv "$file.partial" "$file"
echo "$(date -u +%FT%TZ) wrote $file ($(du -h "$file" | cut -f1))"

find backups -name 'app-*.db' -mtime +"$KEEP_DAYS" -delete
