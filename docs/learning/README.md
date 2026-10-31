# Learning docs: why the template is built the way it is

The rest of `docs/` says **what** the template does and **how** to use it. These docs say **why**:
why each library, pattern and folder was picked, what else was on the table, and what the choice
costs. They are written to learn from, so each one explains the idea behind a decision before the
decision itself, and points at the real code in this repository.

Every release adds one doc here. That is a standing rule for the project: a release is not done
until its learning doc is written.

| Release | Version | Doc | What you learn |
| --- | --- | --- | --- |
| R01 | 0.1.0 | [Core API](r01-core-api.md) | `dotnet new` templates, minimal APIs, feature folders, options validation, ProblemDetails, security headers, rate limiting, OpenTelemetry, testing a template, containers |
| R02 | 0.2.0 | [Accounts](r02-accounts.md) | ASP.NET Core Identity, bearer tokens vs cookies, CSRF, external sign-in and account takeover, OIDC, roles vs permissions, API keys, MFA and passkeys |
| R03 | 0.3.0 | [Data and API depth](r03-data-api.md) | EF Core interceptors, soft delete, audit history, optimistic concurrency, ETags, idempotency keys, streaming, API versioning, three database providers, Dapper |
| R04 | 0.4.0 | [Jobs, worker and Aspire](r04-jobs-worker-aspire.md) | Hosted services, job queues, Hangfire vs in-process, at-least-once delivery and idempotency, distributed locks, splitting a worker, .NET Aspire |
| R05 | 0.5.0 | [Generator tooling](r05-generator-tooling.md) | Why a CLI on top of `dotnet new`, catalogs and rules, presets, specs, pairwise testing, the three-way merge behind `ek update` |
| R06 | 0.6.0 | [Files and documents](r06-files-documents.md) | Object storage and S3, signed links, resumable uploads, trusting bytes not names, choosing libraries by licence, CSV injection, PDF fonts |
| R07 | 0.7.0 | [Communication and real-time](r07-communication-realtime.md) | Resilient HTTP clients, SSRF, SignalR vs SSE vs WebSockets, protocol versioning, WebSocket hijacking, back-pressure, signed webhooks and replays, a database outbox, push and SMS without SDKs |

## How to read them

- Read them in order the first time. Each release builds on the one before, and later docs refer
  back ("the interceptor from R03") instead of explaining again.
- Each doc has the same shape: what the release added, then one section per decision with
  **The choice**, **Why**, **Alternatives** and **Trade-offs**, a short code excerpt from the
  repository, and a "Try it" list at the end.
- Code excerpts are trimmed. The file path above each one points at the full source under
  `templates/ek-api/` (the template) or `src/` (the `ek` tool). Lines starting with `#if` are
  template conditions, explained in R01.
- When a doc says "we" it means the template's authors: decisions were made with Erkman in the
  project thread and the reasons recorded at the time.

## Writing the next one

For a new release, copy the shape of the latest doc into `rNN-<topic>.md` and add a row to the
table above. Cover every choice a reader might question: each new library (and the ones rejected),
each pattern, each folder or project added, and each rule added to the catalog. Prefer one honest
trade-off over a list of benefits.
