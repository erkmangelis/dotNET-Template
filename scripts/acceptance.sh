#!/usr/bin/env bash
# Acceptance test for one option combination of the ek-api template.
#
# Generates a project outside the source tree from the packed template, then checks what
# "supported" means for this template:
#   restore, build with warnings as errors, format check, all tests, no leftovers from
#   unselected options, a deliberate style violation fails the build, and (with --compose)
#   docker compose starts, /health/ready answers 200, with --auth identity|external and
#   --email smtp a confirmation email reaches the bundled Mailpit, with --auth none and any
#   --jobs but none (or --worker) a background job imports notes in the container (the worker's,
#   with --worker), and with --auth none and --storage s3 a file uploads to the bundled storage and
#   downloads again, through the API and through a signed link.
#
# With --update-from, the project is first generated from that older package (with the options it
# already had), committed with a file of the team's own, and then brought to the package under test
# with `ek update`. The update must finish without conflicts, keep the team's file, and the result
# must pass every check above. This is how "an old project updates to the current version" is tested.
#
# Usage:
#   scripts/acceptance.sh --auth none|identity|external|oidc --db none|postgres|sqlserver|sqlite \
#       --email none|smtp --jobs none|hosted|hangfire --storage none|s3 --documents false|true \
#       --worker false|true --aspire false|true \
#       --agents none|shared|claude [--package path/to/Erkmangelis.Templates.*.nupkg] [--compose] [--keep] \
#       [--update-from path/to/older/Erkmangelis.Templates.*.nupkg]
set -euo pipefail

AUTH=none DB=none EMAIL=none JOBS=none STORAGE=none DOCUMENTS=false WORKER=false ASPIRE=false AGENTS=shared PACKAGE="" COMPOSE=false KEEP=false UPDATE_FROM=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --auth) AUTH="$2"; shift 2 ;;
    --db) DB="$2"; shift 2 ;;
    --email) EMAIL="$2"; shift 2 ;;
    --jobs) JOBS="$2"; shift 2 ;;
    --storage) STORAGE="$2"; shift 2 ;;
    --documents) DOCUMENTS="$2"; shift 2 ;;
    --worker) WORKER="$2"; shift 2 ;;
    --aspire) ASPIRE="$2"; shift 2 ;;
    --agents) AGENTS="$2"; shift 2 ;;
    --package) PACKAGE="$(cd "$(dirname "$2")" && pwd)/$(basename "$2")"; shift 2 ;;
    --compose) COMPOSE=true; shift ;;
    --keep) KEEP=true; shift ;;
    --update-from) UPDATE_FROM="$(cd "$(dirname "$2")" && pwd)/$(basename "$2")"; shift 2 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

REPO="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d "${RUNNER_TEMP:-/tmp}/ek-acceptance.XXXXXX")"
HIVE="$WORK/hive"
NAME="Acme.Notes"
OUT="$WORK/$NAME"
# The worker runs Hangfire jobs, so --worker means --jobs hangfire.
OPTIONS="--auth $AUTH --db $DB --email $EMAIL --jobs $JOBS --storage $STORAGE --documents $DOCUMENTS --worker $WORKER --aspire $ASPIRE --agents $AGENTS"
if [[ "$WORKER" == true ]]; then JOBS=hangfire; fi
# Every sign-in mode stores users' data, Hangfire stores jobs and file storage keeps file records,
# so any --auth but none, --jobs hangfire and --storage s3 turn PostgreSQL on when --db is none.
EFFECTIVE_DB="$DB"
if [[ "$DB" == none && ( "$AUTH" != none || "$JOBS" == hangfire || "$STORAGE" == s3 ) ]]; then EFFECTIVE_DB=postgres; fi
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

if [[ -z "$UPDATE_FROM" ]]; then
  step "Install $(basename "$PACKAGE") into an isolated template hive"
  dotnet new install "$PACKAGE" --debug:custom-hive "$HIVE" >/dev/null

  step "Generate $OPTIONS"
  # shellcheck disable=SC2086
  dotnet new ek-api -n "$NAME" -o "$OUT" $OPTIONS --debug:custom-hive "$HIVE"
  cd "$OUT"
  git init -q && git add -A
else
  step "Generate with $(basename "$UPDATE_FROM"), using the options it already had"
  dotnet new install "$UPDATE_FROM" --debug:custom-hive "$HIVE" >/dev/null
  help="$(dotnet new ek-api --help --debug:custom-hive "$HIVE")"
  OLD_OPTIONS=""
  for pair in "auth $AUTH" "db $DB" "email $EMAIL" "jobs $JOBS" "storage $STORAGE" "documents $DOCUMENTS" "worker $WORKER" "aspire $ASPIRE" "agents $AGENTS"; do
    # An option whose short name is its name shows as -db, not --db.
    if grep -qE -- "(^|[ ,])--?${pair%% *} " <<<"$help"; then OLD_OPTIONS="$OLD_OPTIONS --$pair"; else [[ "${pair#* }" =~ ^(none|false)$ ]] || fail "the old template has no --${pair%% *}"; fi
  done
  # shellcheck disable=SC2086
  dotnet new ek-api -n "$NAME" -o "$OUT" $OLD_OPTIONS --debug:custom-hive "$HIVE"
  cd "$OUT"
  git init -q && git add -A && git -c user.name=acceptance -c user.email=acceptance@example.com commit -qm "Generated"
  printf '# Team notes\n\nOur own file, which the update must keep.\n' > docs/team-notes.md
  git add -A && git -c user.name=acceptance -c user.email=acceptance@example.com commit -qm "Team notes"

  step "ek update to $(basename "$PACKAGE")"
  dotnet build "$REPO/src/Erkmangelis.Ek" -c Release -p:EkBundleTemplate=false --nologo -v quiet -o "$WORK/ek"
  dotnet "$WORK/ek/Erkmangelis.Ek.dll" update --from "$UPDATE_FROM" --to "$PACKAGE" || fail "ek update did not finish cleanly"
  [[ -f docs/team-notes.md ]] || fail "the update removed the team's own file"
  git add -A
fi

step "No merge conflict markers are left"
if git grep -nE '^(<<<<<<<|>>>>>>>) ' -- . ; then fail "conflict markers left in the output"; fi

step "No template markers or placeholders are left"
if grep -rnE '^\s*(//)?#(if|else|elif|elseif|endif)\b|<!--#(if|else|endif)|MyProject|myproject|EK_[A-Z_]+|ek-app-slug' \
    --exclude-dir=.git . ; then
  fail "template markers or placeholders left in the output"
fi

step "No leftovers from unselected options"
# leftover <pattern> <message> [extra pathspecs to skip]
leftover() { if git grep -nIiE "$1" -- . ':!template-origin.json' ':!**/Data/Migrations/**' "${@:3}"; then fail "$2"; fi; }
if [[ "$EFFECTIVE_DB" == none ]]; then
  leftover 'Npgsql|EntityFramework|ConnectionStrings|postgres|Testcontainers\.PostgreSql|migration' "database files or settings left with --db none"
  [[ ! -e src/$NAME.Api/Data && ! -e .config && ! -e docs/database.md && ! -e scripts ]] || fail "database folders left with --db none"
fi
if [[ "$EFFECTIVE_DB" != postgres ]]; then
  # The SSO guide's Keycloak runs on its own PostgreSQL, whatever the API uses.
  leftover 'Npgsql|PostgreSql|POSTGRES_|pg_dump|xmin' "PostgreSQL code or settings left with --db $EFFECTIVE_DB" ':!docs/enterprise-sso.md'
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
case "$JOBS" in
  none) leftover 'Hangfire|Cronos|IJobQueue|IRecurringJob' "job code or settings left with --jobs none" ;;
  # The background work guide points at --jobs hangfire as the durable choice.
  hosted) leftover 'Hangfire' "Hangfire code or settings left with --jobs hosted" ':!docs/background-work.md' ;;
  hangfire) leftover 'Cronos|InProcessJob|RecurringJobScheduler' "in-process job code left with --jobs hangfire" ;;
esac
if [[ "$STORAGE" == none ]]; then
  leftover 'AWSSDK|RustFS|tusdotnet|SkiaSharp|IObjectStorage|StoredFile|STORAGE_|/v1/files' "file storage code or settings left with --storage none"
  [[ ! -e src/$NAME.Api/Features/Files && ! -e src/$NAME.Api/Infrastructure/Storage && ! -e docs/files.md ]] || fail "file storage folders left with --storage none"
fi
if [[ "$DOCUMENTS" == false ]]; then
  leftover 'MiniExcel|PdfPig|Liberation|Spreadsheets|PdfReports|export/csv' "document code or settings left with --documents false"
  [[ ! -e src/$NAME.Api/Infrastructure/Documents && ! -e docs/documents.md ]] || fail "document folders left with --documents false"
fi
if [[ "$WORKER" == false ]]; then
  leftover "$NAME\.Worker|worker-build|RunServer\": false" "worker project or settings left with --worker false"
  [[ ! -e src/$NAME.Worker ]] || fail "worker folder left with --worker false"
fi
if [[ "$ASPIRE" == false ]]; then
  leftover 'Aspire|\.AppHost' "Aspire AppHost or settings left with --aspire false"
  [[ ! -e src/$NAME.AppHost ]] || fail "AppHost folder left with --aspire false"
fi
case "$AGENTS" in
  none) [[ ! -e AGENTS.md && ! -e CLAUDE.md ]] || fail "agent files left with --agents none" ;;
  shared) [[ -e AGENTS.md && ! -e CLAUDE.md ]] || fail "--agents shared must give AGENTS.md only" ;;
  claude) [[ -e AGENTS.md && -e CLAUDE.md ]] || fail "--agents claude must give AGENTS.md and CLAUDE.md" ;;
esac
grep -q "\"auth\": \"$AUTH\"" template-origin.json && grep -q "\"db\": \"$EFFECTIVE_DB\"" template-origin.json \
  && grep -q "\"email\": \"$EMAIL\"" template-origin.json && grep -q "\"jobs\": \"$JOBS\"" template-origin.json \
  && grep -q "\"storage\": \"$STORAGE\"" template-origin.json && grep -q "\"documents\": $DOCUMENTS" template-origin.json \
  && grep -q "\"worker\": $WORKER" template-origin.json && grep -q "\"aspire\": $ASPIRE" template-origin.json \
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
  if [[ "$STORAGE" == s3 ]]; then
    printf 'STORAGE_ACCESS_KEY=%s\nSTORAGE_SECRET_KEY=%s\n' "$(openssl rand -hex 10)" "$(openssl rand -hex 20)" >> .env
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

  if [[ "$JOBS" != none && "$AUTH" == none ]]; then
    step "A background job imports notes"
    title="Acceptance import $(openssl rand -hex 8)"
    status="$(curl -s -o /dev/null -w '%{http_code}' -H 'Content-Type: application/json' \
      -d "{\"notes\":[{\"title\":\"$title\"}]}" http://localhost:8080/v1/notes/imports)"
    [[ "$status" == 202 ]] || { docker compose logs api; fail "the import answered $status"; }
    query="search=$(sed 's/ /%20/g' <<<"$title")"
    for _ in $(seq 1 30); do
      if curl -fsS "http://localhost:8080/v1/notes?$query" | grep -q "$title"; then break; fi
      sleep 1
    done
    curl -fsS "http://localhost:8080/v1/notes?$query" | grep -q "$title" \
      || { docker compose logs api worker 2>/dev/null || docker compose logs api; fail "the import job did not create the note"; }
  fi

  if [[ "$STORAGE" == s3 && "$AUTH" == none ]]; then
    step "A file uploads to the storage and downloads again"
    printf 'acceptance %s\n' "$(openssl rand -hex 8)" > "$WORK/upload.txt"
    response="$(curl -fsS -F "file=@$WORK/upload.txt;type=text/plain" http://localhost:8080/v1/files)" \
      || { docker compose logs api storage; fail "the upload failed"; }
    id="$(sed -E 's/.*"id":"([^"]+)".*/\1/' <<<"$response")"
    curl -fsS "http://localhost:8080/v1/files/$id/content" | cmp -s - "$WORK/upload.txt" || fail "the download through the API differs"
    link="$(curl -fsS "http://localhost:8080/v1/files/$id/link" | sed -E 's/.*"url":"([^"]+)".*/\1/')"
    curl -fsS "$link" | cmp -s - "$WORK/upload.txt" || fail "the download through the signed link differs"
  fi
fi

echo
echo "PASSED: $OPTIONS"
