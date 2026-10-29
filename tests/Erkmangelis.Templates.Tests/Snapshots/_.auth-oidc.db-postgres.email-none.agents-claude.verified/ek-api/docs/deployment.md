# Deployment: Raspberry Pi 5 or a small VPS

The API ships as one container image for `linux/amd64` and `linux/arm64`, run with Docker Compose.
HTTPS comes from a Cloudflare Tunnel, so the machine needs no open inbound ports and no certificate
management. Any other TLS-terminating proxy (Caddy, Traefik, Tailscale Funnel) works the same way.

```text
client --HTTPS--> Cloudflare --tunnel--> cloudflared --HTTP--> api:8080
                                                                   |
                                                                   db (PostgreSQL 18)
```

## 1. Prepare the machine

- Raspberry Pi 5 with 64-bit Raspberry Pi OS, or a VPS with Debian or Ubuntu.
- Install Docker Engine and the compose plugin: <https://docs.docker.com/engine/install/>.
- On a Pi, boot from an SSD rather than the SD card if the database is busy.

## 2. Get the code and secrets on the machine

```bash
sudo mkdir -p /srv/acme-notes && sudo chown "$USER" /srv/acme-notes
git clone <your repository> /srv/acme-notes && cd /srv/acme-notes
cp .env.example .env && chmod 600 .env
```

Fill in `.env`: `POSTGRES_PASSWORD` (`openssl rand -base64 32`) and, for step 4, `TUNNEL_TOKEN`.

## 3. Start it

```bash
docker compose up -d --build
curl -fsS http://localhost:8080/health/ready
```

`migrate` applies database migrations and exits; `api` starts after it succeeds.
Building on the Pi works but is slow. To build elsewhere instead, push images from CI (the
`container` workflow publishes to GitHub Container Registry on every `v*` tag) and set
`API_IMAGE=ghcr.io/<owner>/<repo>` and `API_TAG=<version>` in `.env`, then
`docker compose pull && docker compose up -d`.

## 4. HTTPS through a Cloudflare Tunnel

1. In the Cloudflare dashboard: Zero Trust, Networks, Tunnels, create a tunnel and copy its token.
2. Add a public hostname, for example `api.example.com`, with service `http://api:8080`.
3. Put the token in `.env` as `TUNNEL_TOKEN` and start the tunnel profile:

```bash
docker compose --profile tunnel up -d
curl -fsS https://api.example.com/health/ready
```

The API trusts `X-Forwarded-For` and `X-Forwarded-Proto` only from the compose network
(`ForwardedHeaders__KnownNetworks__0` in `compose.yaml`), so client IPs in logs and in the rate
limiter are the real ones. HSTS is sent on every response outside Development.

You can remove the `ports:` mapping from the `api` service once the tunnel works, so the API is
reachable only through it.

## 5. Keep it running

- Updates: `git pull && docker compose up -d --build` (or `pull` with CI images).
- Logs: `docker compose logs -f api`. Logs are JSON, one event per line, with trace ids.
- Backups: schedule `scripts/backup.sh` as described in [database.md](database.md).
- Telemetry: set `OTEL_EXPORTER_OTLP_ENDPOINT` in `.env` to send traces, metrics and logs to an
  OpenTelemetry collector.
- Stops are graceful: requests in flight get up to 30 seconds (`Api__ShutdownTimeoutSeconds`).

## Settings reference

All settings can be set as environment variables with `__` between sections.

| Setting | Default | What it does |
| --- | --- | --- |
| `Api__ExposeOpenApi` | `false` (`true` in Development) | Serves `/openapi/v1.json` and the Scalar UI at `/scalar` |
| `Api__UseHttpsRedirection` | `true` (`false` in compose) | Redirects plain HTTP to HTTPS |
| `Cors__AllowedOrigins__0` | none | Browser origins allowed to call the API |
| `Cors__AllowCredentials` | `false` | Let those origins send cookies (cookie sessions from another origin) |
| `RateLimiting__PermitLimit` | `100` | Requests per caller per window |
| `RateLimiting__WindowSeconds` | `60` | Rate limit window |
| `ForwardedHeaders__KnownNetworks__0` | none | Proxy networks trusted for `X-Forwarded-*` |
| `ConnectionStrings__Default` | none, required | PostgreSQL connection string |
| `Oidc__Authority` | none, required | The OpenID Connect issuer that owns the accounts |
| `Oidc__Audience` | none, required | The `aud` value in access tokens for this API |
| `Oidc__RoleClaim` | `roles` | Where tokens keep roles, for example `realm_access.roles` for Keycloak |
