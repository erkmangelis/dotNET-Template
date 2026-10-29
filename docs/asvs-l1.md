# OWASP ASVS Level 1 checklist

How a project generated from `ek-api` 0.1 stands against
[OWASP ASVS 5.0](https://owasp.org/www-project-application-security-verification-standard/)
Level 1, chapter by chapter. Level 1 is the floor every internet-facing API should meet.

- **Template**: the generated code meets it, and a test or setting shows where.
- **Shared**: the template gives the mechanism; the project must keep using it correctly.
- **Project**: depends on what the project builds; nothing in the template can decide it.
- **N/A**: does not apply to an HTTP API with these options.

Rows marked *identity* apply with `--auth identity`. Re-check this list at every release; the plan
moves the template to Level 2 at v1.0.

| ASVS 5.0 chapter | Status | Where it is handled |
| --- | --- | --- |
| V1 Encoding and sanitization | Template | JSON only through System.Text.Json; EF Core parameterizes every query; no HTML rendering. Raw SQL is the project's to avoid or parameterize. |
| V2 Validation and business logic | Shared | DataAnnotations on request contracts, enforced by built-in validation (`Invalid_input_answers_400_with_the_failing_fields`); paging bounds; per-caller rate limits; request body limit 1 MB. Business rules are the project's. |
| V3 Web frontend security | Template | Responses carry `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`, a deny-all CSP, COOP and CORP (`Responses_carry_the_security_headers`); HSTS outside Development; CORS denies all origins until configured, never `*`. |
| V4 API and web service | Template | Versioned `/v1` routes, correct status codes, ProblemDetails for every error, only declared fields bound (`Fields_the_server_owns_cannot_be_set_by_the_client`), OpenAPI off in production. |
| V5 File handling | N/A | No uploads in R01 (files arrive in R06). |
| V6 Authentication *identity* | Template | ASP.NET Core Identity: PBKDF2 password hashing, 12-character minimum, lockout after 5 failures (`Repeated_wrong_passwords_lock_the_account`), confirmed email required, generic login errors. Breached-password checks and MFA come in R02. |
| V7 Session management *identity* | Template | Bearer tokens expire after 60 minutes and refresh tokens after 14 days; signing keys persist in the database so restarts do not sign everyone out. No cookies in R01. |
| V8 Authorization *identity* | Shared | Notes require a signed-in user; the owner policy answers 404 for other users' notes (`Another_users_note_answers_404`, `The_list_holds_only_the_callers_notes`). Every new feature must add its own policy and the same tests. |
| V9 Self-contained tokens *identity* | Template | Identity bearer tokens are protected by ASP.NET Core Data Protection, not unsigned JWTs. |
| V10 OAuth and OIDC | N/A | Social and OIDC sign-in arrive in R02. |
| V11 Cryptography | Template | Only platform cryptography (Identity, Data Protection, TLS); no custom algorithms. Data Protection keys are stored in PostgreSQL unencrypted at rest; encrypt them with a certificate if the database is shared. |
| V12 Secure communication | Shared | HTTPS redirect and HSTS in the app; TLS terminates at the Cloudflare Tunnel; forwarded headers trusted only from the compose network. Plain HTTP must never be exposed beyond the host. |
| V13 Configuration | Template | Secrets only in user secrets or environment variables, never in appsettings; settings validated on start; no `Server` header; non-root container; Dependabot for NuGet, Docker and Actions. |
| V14 Data protection | Project | The template stores no sensitive data beyond accounts. Decide retention, backups encryption and personal data handling per project. Backups are written by `scripts/backup.sh`; copy them off the host. |
| V15 Secure coding and architecture | Shared | Pinned SDK, central package versions, analyzers with warnings as errors in CI, no commercial-licence dependencies, signed build provenance for the template package and the container images. |
| V16 Security logging and error handling | Template | Structured logs with trace ids; errors return ProblemDetails without stack traces outside Development; logs never hold passwords or tokens (`Logs_never_contain_passwords_or_tokens`); the production email sender logs that a message was dropped, never its link. |
| V17 WebRTC | N/A | Not used. |

Each named test lives in the generated `tests/<Name>.Api.IntegrationTests` project and runs in CI
for every supported combination.
