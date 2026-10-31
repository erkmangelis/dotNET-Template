#!/usr/bin/env bash
# Adds a migration with the same name to every migration set the template ships, generated from
# the current template. Run it after changing the EF model, then review the diff.
#
# Usage: scripts/add-migration.sh <Name>
#        ONLY='webhooks$' scripts/add-migration.sh <Name>   (only the sets whose folder matches the regex)
#
# Running it again with the name of the newest migration replaces that migration, so a release in
# progress keeps one migration for all its model changes. A set that does not exist yet (a new
# provider) gets a single InitialCreate migration instead, and a set whose model did not change gets
# none.
set -euo pipefail

NAME="${1:?usage: scripts/add-migration.sh <Name>}"
REPO="$(cd "$(dirname "$0")/.." && pwd)"
TEMPLATE="$REPO/templates/ek-api"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/ek-migrations.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

# Migration set (folder under .migrations), the options that generate its model, and for a feature
# with a context of its own, that context and its migrations folder.
APP="AppDbContext|Data/Migrations"
SETS=()
for db in postgres sqlserver sqlite; do
  SETS+=("$db/notes|--auth none --db $db|$APP" "$db/identity|--auth identity --db $db|$APP" "$db/oidc|--auth oidc --db $db|$APP")
  # --storage s3 adds the file records.
  SETS+=("$db/notes-files|--auth none --db $db --storage s3|$APP" "$db/identity-files|--auth identity --db $db --storage s3|$APP" "$db/oidc-files|--auth oidc --db $db --storage s3|$APP")
  # Notifications and webhooks have contexts of their own, whatever the other options are.
  SETS+=("$db/notifications|--auth identity --db $db --notifications true|NotificationsDbContext|Data/Migrations/Notifications")
  SETS+=("$db/webhooks|--auth none --db $db --webhooks true|WebhooksDbContext|Data/Migrations/Webhooks")
done

# Every source folder must exist, or the template does not load.
for entry in "${SETS[@]}"; do mkdir -p "$TEMPLATE/.migrations/${entry%%|*}"; done
dotnet new install "$TEMPLATE" --debug:custom-hive "$WORK/hive" >/dev/null

for entry in "${SETS[@]}"; do
  IFS='|' read -r set options context folder <<<"$entry"
  if [[ -n "${ONLY:-}" && ! "$set" =~ $ONLY ]]; then continue; fi
  echo "==> $set ($options, $context)"
  out="$WORK/$set"
  migration="$NAME"
  compgen -G "$TEMPLATE/.migrations/$set/*.cs" >/dev/null || migration=InitialCreate
  # Named MyProject, so the generated code already carries the template's own names.
  # shellcheck disable=SC2086
  dotnet new ek-api -n MyProject -o "$out" $options --email none --agents none --debug:custom-hive "$WORK/hive" >/dev/null
  (
    cd "$out"
    dotnet tool restore >/dev/null && dotnet restore >/dev/null
    ef() { dotnet ef "$@" --project src/MyProject.Api --context "$context"; }
    if compgen -G "src/MyProject.Api/$folder/*_$migration.cs" >/dev/null; then
      # Rolls the model snapshot back too; --force skips the check against a database.
      ef migrations remove --force >/dev/null
    fi
    if [[ "$migration" == "$NAME" ]] && ef migrations has-pending-model-changes >/dev/null 2>&1; then
      echo "    no model changes"
      exit 0
    fi
    ef migrations add "$migration" --output-dir "$folder" >/dev/null
  )
  rm -rf "${TEMPLATE:?}/.migrations/$set"
  mkdir -p "$(dirname "$TEMPLATE/.migrations/$set")"
  cp -r "$out/src/MyProject.Api/$folder" "$TEMPLATE/.migrations/$set"
done

echo "Done. Review: git diff --stat -- templates/ek-api/.migrations"
