# Load testing

`loadtest/notes.js` is a [k6](https://grafana.com/docs/k6/latest/) script. Each virtual user
creates a note, reads it and lists notes, over and over. It fails when:

- more than 1% of requests fail,
- the 95th percentile response time is over 500 ms, or
- more than 1% of checks (expected status codes) fail.

CI runs it against the production container on every push (`container` job, 10 users for 20
seconds), so a change that makes the API much slower or breaks it under load fails the build.

## Run it

Start the API the way production runs it, with rate limiting off (otherwise the limit is what gets
measured: one client from one IP hits it in seconds):

```bash
echo "RATE_LIMITING_ENABLED=false" >> .env
docker compose up -d --build
docker run --rm -i --network host grafana/k6:2.4.0 run - < loadtest/notes.js
```

Change the load with `-e`: `docker run --rm -i --network host -e VUS=50 -e DURATION=2m grafana/k6:2.4.0 run - < loadtest/notes.js`.
With k6 installed, `k6 run -e VUS=50 loadtest/notes.js` does the same.
<!--#if (UseAuth) -->

The notes endpoints need a caller. Pass a bearer token (`-e TOKEN=...`) or an API key with the
`notes:read` and `notes:write` scopes (`-e API_KEY=...`). Without either, the script measures only
`/health/ready`, which is what CI does.
<!--#endif -->

Put rate limiting back on (`RATE_LIMITING_ENABLED=true`, or remove the line) when you are done.

## A measured number

On a 4 vCPU cloud machine, with the Release build in Docker, PostgreSQL 18 in Docker on the same
machine, no sign-in and the memory cache, 20 virtual users for 30 seconds:

| Requests | Requests per second | p95 response time | Failed |
| --- | --- | --- | --- |
| 26,371 | about 877 | 40.6 ms | 0% |

That is one note created, read and listed per iteration, so about 290 full iterations a second.
Treat it as a starting point, not a promise: the database on the same machine, the payloads and the
hardware all change the result. Measure on your own hardware with your own data, and keep the
numbers you get here so you notice when a change moves them.

## Reading the result

- `http_req_duration` p(95): the response time 95% of requests beat. Watch this more than the average.
- `http_req_failed`: share of requests that failed (status 400 and above, or no answer).
- `http_reqs`: total requests and the rate per second.
- `checks`: share of status checks that passed.

With the `monitoring` profile up, watch the API overview dashboard while the test runs: response
time by route, the thread pool queue and GC show where the time goes.

## Tips

- Run k6 on a different machine from the API for numbers that matter: on the same machine they
  compete for CPU.
- Ramp up instead of jumping: replace the scenario with a `ramping-vus` executor to find the point
  where the p95 starts to climb.
- Never point a load test at a server you do not own, or at production without warning the team.
