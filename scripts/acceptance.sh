#!/usr/bin/env bash
# Acceptance test for one option combination of the ek-api template.
#
# Generates a project outside the source tree from the packed template, then checks what
# "supported" means for this template:
#   restore, build with warnings as errors, format check, all tests, no leftovers from
#   unselected options, a deliberate style violation fails the build, and (with --compose)
#   docker compose starts, /health/ready answers 200 and, with --auth identity|external and
#   --email smtp, a confirmation email reaches the bundled Mailpit.
#
# Usage:
#   scripts/acceptance.sh --auth none|identity|external|oidc --db none|postgres|sqlserver|sqlite \
#       --email none|smtp --agents none|shared|claude \
#       [--package path/to/Erkmangelis.Templates.*.nupkg] [--compose] [--keep]
set -euo pipefail

AUTH=none DB=none EMAIL=none AGENTS=shared PACKAGE="" COMPOSE=false KEEP=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --auth) AUTH="$2"; shift 2 ;;
    --db) DB="$2"; shift 2 ;;
    --email) EMAIL="$2"; shift 2 ;;
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
# Every sign-in mode stores users' data, so any --auth but none turns PostgreSQL on when --db is none.
EFFECTIVE_DB="$DB"; [[ "$AUTH" != none && "$DB" == none ]] && EFFECTIVE_DB=postgres
ACCOUNTS=false; [[ "$AUTH" == identity || "$AUTH" == external ]] && ACCOUNTS=true

cleanup() {
  if [[ "$COMPOSE" == true && -f "$OUT/compose.yaml" ]]; then
    (cd "$OUT" && docker compose --profile '*' down -v --remove-orphans >/dev/null 2>&1) || true
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

OPTIONS="--auth $AUTH --db $DB --email $EMAIL --agents $AGENTS"
step "Generate $OPTIONS"
# shellcheck disable=SC2086
dotnet new ek-api -n "$NAME" -o "$OUT" $OPTIONS --debug:custom-hive "$HIVE"
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
  leftover 'Npgsql|EntityFramework|ConnectionStrings|postgres|Testcontainers\.PostgreSql|migration' "database files or settings left with --db none"
  [[ ! -e src/$NAME.Api/Data && ! -e .config && ! -e docs/database.md && ! -e scripts ]] || fail "database folders left with --db none"
fi
if [[ "$EFFECTIVE_DB" != postgres ]]; then
  leftover 'Npgsql|PostgreSql|POSTGRES_|pg_dump|xmin' "PostgreSQL code or settings left with --db $EFFECTIVE_DB"
fi
if [[ "$EFFECTIVE_DB" != sqlserver ]]; then
  leftover 'SqlServer|SqlClient|MsSql|MSSQL_|sqlcmd' "SQL Server code or settings left with --db $EFFECTIVE_DB"
fi
if [[ "$EFFECTIVE_DB" != sqlite ]]; then
  leftover 'Sqlite|app-data' "SQLite code or settings left with --db $EFFECTIVE_DB"
fi
if [[ "$AUTH" == none ]]; then
  leftover 'Identity|Bearer|RequireAuthorization|OwnerId|AppUser|ApiKey' "identity code or settings left with --auth none"
  [[ ! -e src/$NAME.Api/Features/Auth && ! -e src/$NAME.Api/Features/ApiKeys ]] || fail "auth folders left with --auth none"
fi
if [[ "$ACCOUNTS" == false ]]; then
  leftover 'AppUser|AspNetCore\.Identity|Passkey|BearerToken' "local account code left with --auth $AUTH"
fi
if [[ "$AUTH" != external ]]; then
  leftover 'ExternalAuth|GOOGLE_CLIENT_ID' "external sign-in code or settings left with --auth $AUTH"
fi
if [[ "$AUTH" != oidc ]]; then
  leftover 'JwtBearer|Keycloak|OIDC_AUTHORITY' "OpenID Connect code or settings left with --auth $AUTH"
  [[ ! -e keycloak ]] || fail "keycloak folder left with --auth $AUTH"
fi
if [[ "$EMAIL" == none ]]; then
  leftover 'MailKit|Mailpit|SMTP_HOST' "SMTP code or settings left with --email none"
  [[ ! -e src/$NAME.Api/Infrastructure/Email ]] || fail "email folder left with --email none"
fi
case "$AGENTS" in
  none) [[ ! -e AGENTS.md && ! -e CLAUDE.md ]] || fail "agent files left with --agents none" ;;
  shared) [[ -e AGENTS.md && ! -e CLAUDE.md ]] || fail "--agents shared must give AGENTS.md only" ;;
  claude) [[ -e AGENTS.md && -e CLAUDE.md ]] || fail "--agents claude must give AGENTS.md and CLAUDE.md" ;;
esac
grep -q "\"auth\": \"$AUTH\"" template-origin.json && grep -q "\"db\": \"$EFFECTIVE_DB\"" template-origin.json \
  && grep -q "\"email\": \"$EMAIL\"" template-origin.json \
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
  : > .env
  case "$EFFECTIVE_DB" in
    postgres) echo "POSTGRES_PASSWORD=$(openssl rand -hex 24)" >> .env ;;
    # Upper and lower case, digits and a symbol, as SQL Server's password policy needs.
    sqlserver) printf 'MSSQL_ACCEPT_EULA=Y\nMSSQL_SA_PASSWORD=Acc-%s\n' "$(openssl rand -hex 16)" >> .env ;;
  esac
  if [[ "$AUTH" == oidc ]]; then
    # Tokens are only checked on requests, so the provider need not exist for the API to start.
    printf 'OIDC_AUTHORITY=https://auth.example.com/realms/acme\nOIDC_AUDIENCE=acme-notes-api\n' >> .env
  fi
  PROFILES=()
  if [[ "$EMAIL" == smtp ]]; then
    printf 'EMAIL_FROM=noreply@example.com\nSMTP_HOST=mailpit\nSMTP_PORT=1025\nSMTP_SECURITY=None\n' >> .env
    PROFILES=(--profile mail)
  fi
  docker compose "${PROFILES[@]}" up -d --build
  for _ in $(seq 1 60); do
    if curl -fsS http://localhost:8080/health/ready; then echo; break; fi
    sleep 2
  done
  status="$(curl -s -o /dev/null -w '%{http_code}' http://localhost:8080/health/ready || true)"
  if [[ "$status" != 200 ]]; then docker compose logs; fail "/health/ready answered $status"; fi

  if [[ "$EMAIL" == smtp && "$ACCOUNTS" == true ]]; then
    step "Registering sends a confirmation email through SMTP"
    status="$(curl -s -o /dev/null -w '%{http_code}' -H 'Content-Type: application/json' \
      -d '{"email":"acceptance@example.com","password":"Acceptance-Passw0rd!"}' http://localhost:8080/v1/auth/register)"
    [[ "$status" == 200 ]] || { docker compose logs api; fail "registration answered $status"; }
    for _ in $(seq 1 30); do
      if curl -fsS http://localhost:8025/api/v1/messages | grep -q 'acceptance@example.com'; then break; fi
      sleep 1
    done
    curl -fsS http://localhost:8025/api/v1/messages | grep -q 'acceptance@example.com' \
      || { docker compose logs api; fail "no confirmation email reached Mailpit"; }
  fi
fi

echo
echo "PASSED: $OPTIONS"
