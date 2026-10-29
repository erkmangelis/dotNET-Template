#!/usr/bin/env bash
#if (UsePostgres)
# Nightly PostgreSQL backup for the compose deployment. See docs/database.md.
#endif
#if (UseSqlServer)
# Nightly SQL Server backup for the compose deployment. See docs/database.md.
#endif
#if (UseSqlite)
# Nightly SQLite backup for the compose deployment. See docs/database.md.
#endif
#   crontab -e
#   15 3 * * * cd /srv/ek-app-slug && ./scripts/backup.sh >> backups/backup.log 2>&1
set -euo pipefail

cd "$(dirname "$0")/.."
KEEP_DAYS="${KEEP_DAYS:-14}"
mkdir -p backups

#if (UsePostgres)
file="backups/app-$(date -u +%Y%m%dT%H%M%SZ).dump"
# Custom format: compressed, and restorable table by table with pg_restore.
docker compose exec -T db pg_dump --username app --dbname app --format custom > "$file.partial"
mv "$file.partial" "$file"
#endif
#if (UseSqlServer)
name="app-$(date -u +%Y%m%dT%H%M%SZ).bak"
file="backups/$name"
# A full, compressed backup written inside the container, then copied out and removed there.
docker compose exec -T db bash -c "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"\$MSSQL_SA_PASSWORD\" -C -b \
  -Q \"BACKUP DATABASE [app] TO DISK = N'/var/opt/mssql/data/$name' WITH COMPRESSION, INIT\" > /dev/null"
docker compose cp "db:/var/opt/mssql/data/$name" "$file.partial"
docker compose exec -T db rm "/var/opt/mssql/data/$name"
mv "$file.partial" "$file"
#endif
#if (UseSqlite)
file="backups/app-$(date -u +%Y%m%dT%H%M%SZ).db"
# SQLite's online backup: a consistent copy while the API keeps writing.
docker run --rm -v ek-app-slug_app-data:/data -v "$PWD/backups:/backups" alpine:3.22 \
  sh -c "apk add --no-cache --quiet sqlite && sqlite3 /data/app.db \".backup '/backups/$(basename "$file").partial'\""
mv "$file.partial" "$file"
#endif
echo "$(date -u +%FT%TZ) wrote $file ($(du -h "$file" | cut -f1))"

#if (UsePostgres)
find backups -name 'app-*.dump' -mtime +"$KEEP_DAYS" -delete
#endif
#if (UseSqlServer)
find backups -name 'app-*.bak' -mtime +"$KEEP_DAYS" -delete
#endif
#if (UseSqlite)
find backups -name 'app-*.db' -mtime +"$KEEP_DAYS" -delete
#endif
