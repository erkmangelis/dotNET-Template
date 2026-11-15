# Monitoring

The API records traces, metrics and logs with OpenTelemetry (`Infrastructure/Observability.cs`).
The `monitoring` compose profile runs a full stack to look at them, on the same machine:

```bash
docker compose --profile monitoring up -d --build
```

Open Grafana at http://localhost:3000 (user `admin`, password `GRAFANA_ADMIN_PASSWORD` from `.env`)
and pick the **API overview** dashboard.

## What runs

```text
api ──OTLP (traces, logs)──▶ otel-collector ──▶ tempo   (traces)
                                            └─▶ loki    (logs)
api :9464/metrics ◀──scrape── prometheus ──▶ alertmanager
                                   ▲
grafana ─── reads prometheus, tempo, loki, alertmanager
```

| Service | Image | What it does | Port on the host |
| --- | --- | --- | --- |
| `otel-collector` | OpenTelemetry Collector | Receives OTLP and forwards traces and logs | 4317, 4318 |
| `prometheus` | Prometheus 3 | Scrapes metrics, keeps them `PROMETHEUS_RETENTION` (15 days), evaluates alerts | 9090 |
| `alertmanager` | Alertmanager | Groups firing alerts and sends notifications | 9093 |
| `tempo` | Grafana Tempo | Stores traces (14 days) | none |
| `loki` | Grafana Loki | Stores logs (7 days) | none |
| `grafana` | Grafana | Dashboards, trace and log search | 3000 |

Every port listens on 127.0.0.1 only. Reach Grafana from elsewhere through an SSH tunnel or a
Cloudflare Access application, never by opening the port.

## Metrics: Prometheus pulls

With `Monitoring__Prometheus__Enabled=true` (compose sets it), the API serves `/metrics` on a second
port, `Monitoring__Prometheus__Port` (9464), and answers 404 for `/metrics` on the main port, so
the tunnel or load balancer never publishes it. Keep 9464 off the internet.

Traces and logs still go out over OTLP, and metrics then do not, so nothing is counted twice.
Without the setting, everything goes over OTLP to `OTEL_EXPORTER_OTLP_ENDPOINT` as before, which
suits a hosted backend (Grafana Cloud, Honeycomb, Azure Monitor).

The metrics are the standard ASP.NET Core, HttpClient and .NET runtime ones, for example
`http_server_request_duration_seconds` (by route and status) and `dotnet_gc_heap_total_allocated_bytes_total`.

## Traces and logs together

Every log line carries the trace id of the request that wrote it. In Grafana, a trace in Tempo has a
"Logs for this span" link, and a log line in Loki has a link to its trace.

## Alerts

`monitoring/prometheus/alerts.yml` holds four rules:

| Alert | Fires when |
| --- | --- |
| `ApiDown` | Prometheus cannot scrape the API or worker for 2 minutes |
| `HighErrorRate` | More than 5% of requests answer 5xx for 5 minutes |
| `SlowResponses` | The 95th percentile response time is over 1 second for 10 minutes |
| `ThreadPoolStarved` | More than 100 work items wait for a thread pool thread for 5 minutes |

`monitoring/prometheus/alerts.test.yml` tests them; CI runs
`promtool test rules monitoring/prometheus/alerts.test.yml`. Change a threshold there too.

Alertmanager starts with a receiver that sends nothing, so alerts show in Grafana and at
http://localhost:9093 only. To get notified, uncomment one of the examples in
`monitoring/alertmanager/alertmanager.yml` (email, Slack, a webhook) and fill it in.

## Add a dashboard

Build it in Grafana, then Export, Save to file, and put the JSON in `monitoring/grafana/dashboards/`.
Dashboards there load at start and cannot be changed in the UI, so the repository stays the source.

## Resources

The whole profile needs about 1 GB of memory at rest. On a Raspberry Pi with 4 GB, lower
`PROMETHEUS_RETENTION` if the disk is small, or send telemetry to a hosted backend instead.

## Licences

Prometheus, Alertmanager and the collector are Apache 2.0. Grafana, Tempo and Loki are AGPL 3.0:
running them unchanged as separate services, as here, puts no obligation on your code. Changing
their source and offering it to others over a network would.
