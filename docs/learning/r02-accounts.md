# R02, Accounts (0.2.0): why sign-in works like this

R02 turned R01's single `--auth identity` into a full accounts release. It added two more ways to
sign in (`--auth external` and `--auth oidc`), real email (`--email smtp`), browser cookie sessions,
MFA and passkeys, roles and permissions, API keys and user profiles.

Authentication is where small mistakes become account takeovers, so this doc spends more time on
*attacks* than the others: most choices here exist because a specific attack works without them.

## Contents

1. [Four auth modes instead of one](#1-four-auth-modes-instead-of-one)
2. [ASP.NET Core Identity, not IdentityServer or a hand-rolled login](#2-aspnet-core-identity-not-identityserver-or-a-hand-rolled-login)
3. [Bearer tokens and cookie sessions, side by side](#3-bearer-tokens-and-cookie-sessions-side-by-side)
4. [CSRF and the antiforgery token](#4-csrf-and-the-antiforgery-token)
5. [External sign-in and the unverified-email takeover](#5-external-sign-in-and-the-unverified-email-takeover)
6. [OIDC: the API as a resource server](#6-oidc-the-api-as-a-resource-server)
7. [Roles map to permissions; endpoints check permissions](#7-roles-map-to-permissions-endpoints-check-permissions)
8. [API keys: format, hashing, scopes](#8-api-keys-format-hashing-scopes)
9. [MFA and passkeys](#9-mfa-and-passkeys)
10. [Email behind a transport interface](#10-email-behind-a-transport-interface)
11. [Testing ten combinations instead of thirty](#11-testing-ten-combinations-instead-of-thirty)

---

## 1. Four auth modes instead of one

| `--auth` | Who stores the accounts | Typical project |
| --- | --- | --- |
| `none` | Nobody | Internal tool behind a VPN, public read-only API |
| `identity` | Your database (passwords, MFA, passkeys) | A product with its own sign-up |
| `external` | Your database, plus Google, Apple, Microsoft logins | A consumer product where "Sign in with Google" matters |
| `oidc` | An identity provider (Keycloak, Entra ID, Auth0) | Company apps, enterprise SSO, several apps sharing one login |

**Why these four.** They are genuinely different architectures, not variations. `identity` and
`external` mean *your* API is the identity provider and must protect passwords. `oidc` means your
API never sees a password; it only checks tokens someone else issued. Choosing between them
early matters because migrating users between the two later is painful.

**Why every mode turns on PostgreSQL.** Accounts, API keys and profiles need storage. The computed
symbol from R01 does it, so `--auth oidc --db none` still works. OIDC needs a database too: the
provider owns the login, but your API still stores profiles and API keys.

## 2. ASP.NET Core Identity, not IdentityServer or a hand-rolled login

**The choice.** ASP.NET Core Identity with its built-in API endpoints (`MapIdentityApi`):
register, login, refresh, confirm email, reset password, 2FA management.

**Why.** Identity is Microsoft's, free, maintained with the framework, and gets the hard parts
right: password hashing with PBKDF2 and per-user salts, lockout after failed attempts,
security stamps that invalidate sessions after a password change, and tokens for email
confirmation and reset.

**Alternatives.**

- **Hand-rolled login.** Never. Password storage, timing-safe comparison, lockout and reset tokens
  are each easy to get subtly wrong.
- **Duende IdentityServer.** A full OAuth/OIDC server. Powerful, but commercially licensed (the
  licence rule from R01), and only needed if *your* app must issue tokens to *other* apps.
- **OpenIddict.** The free alternative to IdentityServer. Considered for the same "issue tokens to
  others" need; not needed for an API that only signs in its own users.

**Trade-offs.** Identity's API endpoints are opinionated (their routes and response shapes are
fixed). The template wraps or adds endpoints where needed (external sign-in, passkeys, profiles)
rather than replacing them.

## 3. Bearer tokens and cookie sessions, side by side

**The choice.** The same login endpoint returns a bearer token by default, or sets a cookie with
`?useCookies=true`.

**Why both.** A mobile app or another server wants a token it puts in the `Authorization` header.
A browser frontend (the plan names Next.js) is safer with an **HttpOnly cookie**: JavaScript cannot
read it, so a cross-site scripting bug cannot steal the session. Storing tokens in `localStorage`
is the common alternative and the common mistake.

```csharp
// templates/ek-api/src/MyProject.Api/Features/Auth/BrowserSessions.cs
// __Host- cookies are Secure, have no Domain and cover the whole host, so nothing else can set or widen them.
options.Cookie.Name = "__Host-MyProject";
options.Cookie.HttpOnly = true;
options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
options.Cookie.SameSite = SameSiteMode.Strict;
```

Each line blocks something specific:

- `HttpOnly`: scripts cannot read the cookie (XSS cannot steal it).
- `Secure`: sent over HTTPS only.
- `SameSite=Strict`: the browser does not send it on requests started by other sites.
- `__Host-` prefix: the browser refuses the cookie unless it is Secure, has no `Domain` and has
  `Path=/`, so a compromised sibling subdomain cannot plant or overwrite it.

**Why "same-site".** `app.example.com` and `api.example.com` are the same *site* (same registrable
domain), so `SameSite=Strict` cookies still flow between them. A frontend on a completely different
domain would need `SameSite=None`, which gives up most of the protection. The docs say so.

## 4. CSRF and the antiforgery token

**The attack.** Cross-site request forgery: a malicious page makes your browser send a request to
the API, and the browser attaches your cookie automatically. Bearer tokens are immune (the attacker
cannot add the header); cookies are not.

**The defence.** `SameSite=Strict` already stops most of it. As a second layer, every *unsafe*
request (POST, PUT, DELETE) authenticated by the cookie must also send a token in the
`X-CSRF-TOKEN` header, fetched from `GET /v1/auth/antiforgery`. A cross-site page cannot read that
token, so it cannot forge the header.

```csharp
// BrowserSessions.cs
if (IsUnsafe(context.Request.Method) && IsCookieSession(context))
{
    if (!await antiforgery.IsRequestValidAsync(context)) { /* 400 ProblemDetails */ }
}
```

**Why only cookie requests.** Requests with a bearer token or API key are not vulnerable to CSRF,
and asking them for a token would break every non-browser client.

**Why it runs before authorization.** A policy that accepts several schemes (cookie, bearer, API
key) replaces the authentication result this check reads, so the order in `UseApiDefaults` matters,
and a comment says so.

## 5. External sign-in and the unverified-email takeover

**The choice.** The frontend gets an **ID token** from Google, Apple or Microsoft (using their own
SDKs) and posts it to the API. The API validates it (signature against the provider's published
keys, issuer, audience, expiry, nonce) and signs the user in.

**Why ID tokens and not the server-side redirect flow.** ASP.NET's classic external login
(`AddGoogle()`) is a redirect dance designed for server-rendered pages. An API serving a SPA or a
mobile app is better served by "frontend signs in with the provider, API checks the proof".

**The attack this release is built around.** Suppose `alice@example.com` has an account here. An
attacker creates an account at some provider using `alice@example.com` *without verifying it*, then
signs in here with that provider. A naive implementation looks up the account by email, links it,
and the attacker is now Alice.

```csharp
// templates/ek-api/src/MyProject.Api/Features/Auth/ExternalAuthEndpoints.cs
// A new login. Only an email the provider verified may create an account or join an existing one;
// otherwise anyone could add an unverified address at the provider and take over that account.
if (!identity.EmailVerified || identity.Email is null)
{
    return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Email not verified", ...);
}
...
else if (!user.EmailConfirmed)
{
    // Nobody ever proved they own this unconfirmed account. The verified email wins, so whatever
    // credentials the account had are removed before the provider is linked.
    await ResetUnprovenAccountAsync(users, apiKeys, user, cancellationToken);
}
```

The second half covers the reverse attack: the attacker registers `alice@example.com` here with a
password first (never confirming it) and waits for Alice to sign in with Google. Without the
reset, the attacker's password would stay on Alice's account.

**Other rules:** a user can link and unlink providers, but never unlink the last way to sign in.
Each path has a test, including both takeover attempts.

## 6. OIDC: the API as a resource server

```csharp
// templates/ek-api/src/MyProject.Api/Features/Auth/OidcSetup.cs
/// The API is a resource server: it validates the provider's access tokens (issuer, audience,
/// lifetime and signature) and never sees passwords.
services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
jwt.Authority = oidc.Value.Authority;
jwt.Audience = oidc.Value.Audience;
jwt.TokenValidationParameters.ClockSkew = TimeSpan.FromMinutes(1);
```

**Why.** In OAuth terms there are three roles: the *client* (frontend), the *authorization server*
(Keycloak, Entra ID) and the *resource server* (this API). Keeping the API a pure resource server
means it can be used with any standards-compliant provider by changing two settings, and all the
login UX (MFA, social login, SAML federation) is the provider's job.

**Why audience matters.** Without an audience check, a token issued for *another* app at the same
provider would be accepted here.

**`ClockSkew` of one minute.** The default is five minutes, which keeps an expired token valid for
five extra minutes. One minute tolerates small clock differences without that gap.

**Roles from a configurable claim path.** Every provider puts roles somewhere different (Keycloak:
`realm_access.roles`; Entra ID: `roles`). A setting names the path instead of code per provider.

**Development Keycloak.** Compose profile `sso` starts Keycloak with an imported realm and a known
dev user, so you can sign in locally without a cloud account. Keycloak is open source (Apache-2.0)
and the most common self-hosted choice. Enterprise SSO (a customer's SAML IdP) is a recipe
(`docs/enterprise-sso.md`): Keycloak brokers the customer's SAML into OIDC, so the API never learns
SAML.

## 7. Roles map to permissions; endpoints check permissions

```csharp
// templates/ek-api/src/MyProject.Api/Features/Auth/Permissions.cs
/// What a role may do. Endpoints check permissions, never role names, so changing who can do what is
/// an edit to ByRole alone.
public static readonly IReadOnlyDictionary<string, string[]> ByRole = ...
{
    [Roles.Admin]   = [NotesReadAll, AuditRead, UsersRead, UsersManageRoles],
    [Roles.Auditor] = [NotesReadAll, AuditRead, UsersRead],
};

admin.MapGet("/", ListAllAsync).RequirePermission(Permissions.NotesReadAll);
```

**Why not `[Authorize(Roles = "admin")]`.** Role checks scatter the answer to "who can do X?" across
every endpoint. The day you add a `support` role that may read notes but not change roles, you must
find every check. With permissions, endpoints say *what* they need, and one table says *which roles*
have it.

**How it works.** `PermissionClaims` is an `IClaimsTransformation`: once per request it turns each
role claim into permission claims, and `RequirePermission` is a plain claim check. No database hit
per request.

**Alternatives.** Full policy-based or attribute-based access control (ABAC), or an external engine
like OpenFGA or Casbin. Worth it for complex sharing rules; overkill as a default.

## 8. API keys: format, hashing, scopes

```csharp
// templates/ek-api/src/MyProject.Api/Features/ApiKeys/ApiKeyFormat.cs
/// Keys look like ak_3f9c0e5a7b1d_<43 characters>: a fixed prefix so secret scanners can spot
/// them, a public lookup id, and 256 random bits of secret.
public static byte[] Hash(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));
public static bool Matches(string secret, byte[] secretHash) => CryptographicOperations.FixedTimeEquals(Hash(secret), secretHash);
```

Each piece of the design has a reason:

- **Prefix `ak_`.** GitHub's secret scanning and similar tools can be taught the pattern, so a key
  pushed to a repo is caught. Stripe (`sk_live_`) and GitHub (`ghp_`) do the same.
- **Lookup id.** The server finds the row by the public part, then compares the secret. Without it
  you would have to hash-and-compare against every key.
- **SHA-256, not a password hash.** Passwords are short and guessable, so they need a *slow* hash
  (PBKDF2, bcrypt). A 256-bit random secret cannot be guessed, so a fast hash is safe and keeps
  every request cheap.
- **Stored hashed.** A database leak does not leak usable keys. The key is shown once at creation.
- **`FixedTimeEquals`.** A normal comparison stops at the first different byte, which leaks timing.
- **Scopes, expiry, rotation with overlap, revocation.** A key for a reporting script gets
  `notes:read` only; rotation lets the old key work for a while so deploys do not break.

**Keys only where endpoints opt in.** `AddUserOrKeyPolicy` adds the API key scheme to a policy and
requires the scope. Key management, `/v1/me` and admin endpoints never accept keys: a leaked key
must not be able to mint more keys or act as an admin.

```csharp
.RequireAssertion(context => !IsApiKey(context.User) || context.User.HasClaim(ApiKeyScopes.ClaimType, scope))
```

## 9. MFA and passkeys

**Authenticator-app MFA (TOTP)** with recovery codes is built into Identity; the template wires and
tests it. SMS codes are left out on purpose: they are vulnerable to SIM swapping and cost money.

**Passkeys (WebAuthn).** .NET 10 added passkey support to Identity (schema version 3). A passkey is
a key pair stored on the user's device; the server keeps only the public key. Passkeys cannot be
phished (they are bound to the site's domain) and there is nothing to leak from the server.
They are the direction the whole industry is moving.

**Testing it.** WebAuthn normally needs a real authenticator. The integration tests include a
small *software authenticator* (`TestAuthenticator.cs`) that builds real CBOR-encoded attestations
and signatures, so registration and sign-in are tested end to end in CI.

## 10. Email behind a transport interface

```csharp
// templates/ek-api/src/MyProject.Api/Infrastructure/Email/EmailMessage.cs
internal interface IEmailTransport
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
```

**Why an interface here.** There are genuinely two implementations: SMTP in production and a
log transport in development (prints the link so you can click it). Tests capture messages
instead of sending them. Interfaces with one implementation are ceremony; this one earns its keep.

**MailKit.** The library Microsoft itself recommends over the obsolete `System.Net.Mail.SmtpClient`.
Modern TLS, MIT licence.

**SMTP, not a provider API.** Every provider (SendGrid, Postmark, SES, Mailgun) speaks SMTP, so one
implementation works with all of them. A provider's HTTP API adds features (templates, analytics)
but ties you to that provider.

**HTML-encoded templates.** Any value placed into an email (a display name, a link) is
HTML-encoded, so a user named `<script>` cannot inject markup into emails sent to others.

**Mailpit** in compose (profile `mail`) is a fake SMTP server with a web inbox. The acceptance test
registers a user and checks that the confirmation email arrived there.

**Without `--email`**, confirmation emails outside Development are dropped with a warning. That is
honest: the template does not pretend to send mail it cannot send.

## 11. Testing ten combinations instead of thirty

With four auth modes, two email modes and three agent modes there were dozens of combinations.
But `--agents` only adds or removes `AGENTS.md` and `CLAUDE.md`; it does not change code. So CI ran
the **ten code-relevant** combinations, rotating the `--agents` value across them, and a snapshot
test proves the agents option touches only those two files.

This is the first time the "supported means tested" rule from R01 met combinatorial growth. The
answer here (reason about which options interact) became a general algorithm in R05 (pairwise
testing).

---

## Try it

1. Generate `--auth external`. Read `ExternalSignInTests.cs` and find the two takeover tests.
2. Generate `--auth identity`, sign in with `?useCookies=true`, then send a POST without the
   `X-CSRF-TOKEN` header. You get 400.
3. Create an API key with only `notes:read` and try to create a note with it. Then try
   `GET /v1/me` with it.
4. Run `docker compose --profile sso up` with `--auth oidc` and sign in as `dev@example.com`.

## Further reading

- OWASP Authentication Cheat Sheet: https://cheatsheetseries.owasp.org/cheatsheets/Authentication_Cheat_Sheet.html
- OWASP CSRF Prevention: https://cheatsheetseries.owasp.org/cheatsheets/Cross-Site_Request_Forgery_Prevention_Cheat_Sheet.html
- Passkeys in ASP.NET Core Identity: https://learn.microsoft.com/aspnet/core/security/authentication/passkeys
- OAuth 2.0 roles (RFC 6749 section 1.1): https://www.rfc-editor.org/rfc/rfc6749#section-1.1
