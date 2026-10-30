#!/usr/bin/env bash
# Nightly SQL Server backup for the compose deployment. See docs/database.md.
#   crontab -e
#   15 3 * * * cd /srv/acme-notes && ./scripts/backup.sh >> backups/backup.log 2>&1
set -euo pipefail

cd "$(dirname "$0")/.."
KEEP_DAYS="${KEEP_DAYS:-14}"
mkdir -p backups

name="app-$(date -u +%Y%m%dT%H%M%SZ).bak"
file="backups/$name"
# A full, compressed backup written inside the container, then copied out and removed there.
docker compose exec -T db bash -c "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"\$MSSQL_SA_PASSWORD\" -C -b \
  -Q \"BACKUP DATABASE [app] TO DISK = N'/var/opt/mssql/data/$name' WITH COMPRESSION, INIT\" > /dev/null"
docker compose cp "db:/var/opt/mssql/data/$name" "$file.partial"
docker compose exec -T db rm "/var/opt/mssql/data/$name"
mv "$file.partial" "$file"
echo "$(date -u +%FT%TZ) wrote $file ($(du -h "$file" | cut -f1))"

find backups -name 'app-*.bak' -mtime +"$KEEP_DAYS" -delete
