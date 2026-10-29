#!/usr/bin/env bash
# Adds a migration with the same name to every migration set the template ships, generated from
# the current template. Run it after changing the EF model, then review the diff.
#
# Usage: scripts/add-migration.sh <Name>
#
# Running it again with the name of the newest migration replaces that migration, so a release in
# progress keeps one migration for all its model changes.
set -euo pipefail

NAME="${1:?usage: scripts/add-migration.sh <Name>}"
REPO="$(cd "$(dirname "$0")/.." && pwd)"
TEMPLATE="$REPO/templates/ek-api"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/ek-migrations.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

dotnet new install "$TEMPLATE" --debug:custom-hive "$WORK/hive" >/dev/null

# Migration set (folder under .migrations) and the options that generate its model.
SETS=(
  "notes|--auth none --db postgres"
  "identity|--auth identity --db postgres"
  "oidc|--auth oidc --db postgres"
)

for entry in "${SETS[@]}"; do
  set="${entry%%|*}"
  options="${entry#*|}"
  echo "==> $set ($options)"
  out="$WORK/$set"
  # Named MyProject, so the generated code already carries the template's own names.
  # shellcheck disable=SC2086
  dotnet new ek-api -n MyProject -o "$out" $options --email none --agents none --debug:custom-hive "$WORK/hive" >/dev/null
  (
    cd "$out"
    dotnet tool restore >/dev/null && dotnet restore >/dev/null
    ef=(dotnet ef --project src/MyProject.Api)
    if compgen -G "src/MyProject.Api/Data/Migrations/*_$NAME.cs" >/dev/null; then
      # Rolls the model snapshot back too; --force skips the check against a database.
      "${ef[@]}" migrations remove --force >/dev/null
    fi
    "${ef[@]}" migrations add "$NAME" --output-dir Data/Migrations >/dev/null
  )
  rm -rf "$TEMPLATE/.migrations/$set"
  cp -r "$out/src/MyProject.Api/Data/Migrations" "$TEMPLATE/.migrations/$set"
done

echo "Done. Review: git diff --stat -- templates/ek-api/.migrations"
