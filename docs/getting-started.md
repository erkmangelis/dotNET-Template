# Getting started

This walks you from nothing to a running API with sign-in and a database, then onto a Raspberry Pi
or VPS. It takes about 20 minutes.

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and
[Docker](https://docs.docker.com/get-docker/).

## 1. Install the template

Download `Erkmangelis.Templates.<version>.nupkg` from the
[latest release](https://github.com/erkmangelis/dotNET-Template/releases), then:

```bash
dotnet new install ./Erkmangelis.Templates.0.5.0.nupkg
dotnet new ek-api --help
```

To check the download first: `gh attestation verify Erkmangelis.Templates.0.5.0.nupkg --repo erkmangelis/dotNET-Template`.

## 2. Create a project

```bash
dotnet new ek-api -n Garage --auth identity --db postgres --agents claude
cd Garage
git init && git add -A && git commit -m "Start from ek-api 0.5.0"
```

Or let the `ek` tool ask: `ek new Garage` asks for a preset or each option, and only offers
combinations that are supported ([docs/cli.md](cli.md)).

Pick the options you need:

- `--auth none` for an internal or public read-only API, `--auth identity` for user accounts with
  passwords and passkeys, `--auth external` to add Google, Apple and Microsoft sign-in to those
  accounts, `--auth oidc` when an identity provider (Keycloak, Entra ID, Auth0) owns the accounts.
- `--email smtp` to send confirmation and reset emails through an SMTP server; `none` logs them in
  development only.
- `--db none` keeps notes in memory, handy for a prototype. For real data, `--db postgres` is the
  default choice; `--db sqlserver` when the organisation runs SQL Server (amd64 only, Express
  edition by default); `--db sqlite` for a small single-instance app with no database server.
- `--jobs hosted` for background work that may be lost on a restart (in-process queue and cron
  schedules, no extra service), `--jobs hangfire` for durable jobs with retries and a dashboard,
  stored in the database. Add `--worker` to run them in a separate worker process and container.
- `--aspire` to start the API, the worker and the database with one `dotnet run` during development,
  with a dashboard for their logs and traces.
- `--agents claude` if you use Claude Code, `shared` for other coding agents, `none` for neither.

## 3. Run it locally

```bash
cp .env.example .env
# put a password in .env: POSTGRES_PASSWORD=...
docker compose up -d db
dotnet user-secrets set ConnectionStrings:Default "Host=localhost;Database=app;Username=app;Password=<the same password>" --project src/Garage.Api
dotnet run --project src/Garage.Api
```

Open <http://localhost:5080/scalar>. The app has applied its migrations and is ready.

## 4. Try the API

```bash
curl -s -X POST localhost:5080/v1/auth/register -H 'content-type: application/json' \
  -d '{"email":"me@example.com","password":"Correct-horse-battery-1"}'
```

The confirmation link appears in the `dotnet run` console. Open it, then sign in and create a note:

```bash
token=$(curl -s -X POST localhost:5080/v1/auth/login -H 'content-type: application/json' \
  -d '{"email":"me@example.com","password":"Correct-horse-battery-1"}' | jq -r .accessToken)
curl -s -X POST localhost:5080/v1/notes -H "authorization: Bearer $token" \
  -H 'content-type: application/json' -d '{"title":"First note"}'
curl -s localhost:5080/v1/notes -H "authorization: Bearer $token"
```

## 5. Run the checks CI runs

```bash
dotnet build -warnaserror
dotnet format --verify-no-changes
dotnet test
```

## 6. Add your own feature

Copy the shape of `src/Garage.Api/Features/Notes`: contracts, endpoints, a store, an EF
configuration in `Data/`, then a migration:

```bash
dotnet tool restore
dotnet ef migrations add AddCars --project src/Garage.Api --output-dir Data/Migrations
```

Map the endpoints on the `/v1` group in `Program.cs`, and add an integration test that proves one
user cannot see another user's data.

## 7. Deploy

Push to GitHub, then follow `docs/deployment.md` in your project: Docker Compose on the Pi or VPS,
HTTPS through a Cloudflare Tunnel, nightly backups from `docs/database.md`.

## Updating later

`template-origin.json` records the template version, name and options you started from. Read the
[changelog](../CHANGELOG.md) for what changed between versions, then take a new version's changes
with `ek update` on a clean working tree:

```bash
ek update
git diff        # review; resolve any conflict markers, then build, test and commit
```

It merges the template's changes into your files and never overwrites what you changed. See
[docs/cli.md](cli.md#update-a-project).
