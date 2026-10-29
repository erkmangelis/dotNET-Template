#!/usr/bin/env bash
# Acceptance test for one option combination of the ek-api template.
#
# Generates a project outside the source tree from the packed template, then checks what
# "supported" means for this template:
#   restore, build with warnings as errors, format check, all tests, no leftovers from
#   unselected options, a deliberate style violation fails the build, and (with --compose)
#   docker compose starts and /health/ready answers 200.
#
# Usage:
#   scripts/acceptance.sh --auth none|identity --db none|postgres --agents none|shared|claude \
#       [--package path/to/Erkmangelis.Templates.*.nupkg] [--compose] [--keep]
set -euo pipefail

AUTH=none DB=none AGENTS=shared PACKAGE="" COMPOSE=false KEEP=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --auth) AUTH="$2"; shift 2 ;;
    --db) DB="$2"; shift 2 ;;
    --agents) AGENTS="$2"; shift 2 ;;
    --package) PACKAGE="$2"; shift 2 ;;
    --compose) COMPOSE=true; shift ;;
    --keep) KEEP=true; shift ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

REPO="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d "${RUNNER_TEMP:-/tmp}/ek-acceptance.XXXXXX")"
HIVE="$WORK/hive"
NAME="Acme.Notes"
OUT="$WORK/$NAME"
EFFECTIVE_DB="$DB"; [[ "$AUTH" == identity ]] && EFFECTIVE_DB=postgres

cleanup() {
  if [[ "$COMPOSE" == true && -f "$OUT/compose.yaml" ]]; then
    (cd "$OUT" && docker compose down -v --remove-orphans >/dev/null 2>&1) || true
  fi
  if [[ "$KEEP" == true ]]; then echo "Kept $WORK"; else rm -rf "$WORK"; fi
}
trap cleanup EXIT

step() { echo; echo "==> $*"; }
fail() { echo "FAILED: $*" >&2; exit 1; }

if [[ -z "$PACKAGE" ]]; then
  step "Pack the template"
  dotnet pack "$REPO/Erkmangelis.Templates.csproj" -c Release -o "$WORK/package" --nologo -v quiet
  PACKAGE="$(ls "$WORK"/package/Erkmangelis.Templates.*.nupkg)"
fi

step "Install $(basename "$PACKAGE") into an isolated template hive"
dotnet new install "$PACKAGE" --debug:custom-hive "$HIVE" >/dev/null

step "Generate --auth $AUTH --db $DB --agents $AGENTS"
dotnet new ek-api -n "$NAME" -o "$OUT" --auth "$AUTH" --db "$DB" --agents "$AGENTS" --debug:custom-hive "$HIVE"
cd "$OUT"
git init -q && git add -A

step "No template markers or placeholders are left"
if grep -rnE '^\s*(//)?#(if|else|elif|elseif|endif)\b|<!--#(if|else|endif)|MyProject|myproject|EK_[A-Z_]+|ek-app-slug' \
    --exclude-dir=.git . ; then
  fail "template markers or placeholders left in the output"
fi

step "No leftovers from unselected options"
leftover() { if git grep -nIiE "$1" -- . ':!template-origin.json' ':!**/Data/Migrations/**'; then fail "$2"; fi; }
if [[ "$EFFECTIVE_DB" == none ]]; then
  leftover 'Npgsql|EntityFramework|ConnectionStrings|postgres|Testcontainers|migration' "database files or settings left with --db none"
  [[ ! -e src/$NAME.Api/Data && ! -e .config && ! -e docs/database.md && ! -e scripts ]] || fail "database folders left with --db none"
fi
if [[ "$AUTH" == none ]]; then
  leftover 'Identity|Bearer|RequireAuthorization|OwnerId|AppUser' "identity code or settings left with --auth none"
  [[ ! -e src/$NAME.Api/Features/Auth ]] || fail "auth folder left with --auth none"
fi
case "$AGENTS" in
  none) [[ ! -e AGENTS.md && ! -e CLAUDE.md ]] || fail "agent files left with --agents none" ;;
  shared) [[ -e AGENTS.md && ! -e CLAUDE.md ]] || fail "--agents shared must give AGENTS.md only" ;;
  claude) [[ -e AGENTS.md && -e CLAUDE.md ]] || fail "--agents claude must give AGENTS.md and CLAUDE.md" ;;
esac
grep -q "\"auth\": \"$AUTH\"" template-origin.json && grep -q "\"db\": \"$EFFECTIVE_DB\"" template-origin.json \
  || fail "template-origin.json does not record the options"

step "Restore"
dotnet restore --nologo

step "Build with warnings as errors"
dotnet build --no-restore -c Release -warnaserror --nologo

step "Format check"
dotnet format --verify-no-changes --no-restore

step "Tests"
dotnet test --no-build -c Release

step "A style violation fails the build"
cat > "src/$NAME.Api/StyleViolation.cs" <<'CS'
namespace Acme.Notes.Api;

internal sealed class StyleViolation
{
    private int count;

    public int Next() => ++count;
}
CS
if violation="$(dotnet build "src/$NAME.Api" --no-restore -c Release -warnaserror --nologo 2>&1)"; then
  fail "a private field without the _ prefix did not fail the build"
fi
grep -q "error IDE1006" <<<"$violation" || { echo "$violation"; fail "the build failed, but not on the naming rule"; }
rm "src/$NAME.Api/StyleViolation.cs"

if [[ "$COMPOSE" == true ]]; then
  step "docker compose up and /health/ready"
  if [[ "$EFFECTIVE_DB" == postgres ]]; then
    echo "POSTGRES_PASSWORD=$(openssl rand -hex 24)" > .env
  fi
  docker compose up -d --build
  for _ in $(seq 1 60); do
    if curl -fsS http://localhost:8080/health/ready; then echo; break; fi
    sleep 2
  done
  status="$(curl -s -o /dev/null -w '%{http_code}' http://localhost:8080/health/ready || true)"
  if [[ "$status" != 200 ]]; then docker compose logs; fail "/health/ready answered $status"; fi
fi

echo
echo "PASSED: --auth $AUTH --db $DB --agents $AGENTS"
