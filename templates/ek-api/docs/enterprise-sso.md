# Enterprise single sign-on

Business customers often want their staff to sign in with the company's own identity provider:
Entra ID, Okta, Google Workspace, ADFS, or anything else that speaks SAML 2.0 or OpenID Connect.
This API never talks SAML. It trusts one OpenID Connect provider (`Oidc:Authority`), and that
provider brokers each customer's identity provider. Every sign-in, whatever its origin, reaches the
API as the same kind of access token, so no API code changes per customer.

```text
Customer IdP (SAML or OIDC) ──> your provider (Keycloak) ──OIDC token──> this API
```

The recipe below uses Keycloak, which `docker compose --profile sso up -d keycloak` runs locally
with the realm in `keycloak/realm.json`. Auth0, Zitadel, Entra External ID and WorkOS do the same
job; the steps map one to one.

## 1. Run the provider for real

The compose service is for development only: it keeps everything in memory. In production, run
Keycloak with its own PostgreSQL database and a public HTTPS hostname (`KC_HOSTNAME`), or use a
hosted provider. Then point the API at it:

| Setting | Value |
|---|---|
| `OIDC_AUTHORITY` | `https://auth.example.com/realms/ek-app-slug` |
| `OIDC_AUDIENCE` | `ek-app-slug-api` |
| `OIDC_ROLE_CLAIM` | `realm_access.roles` |

Create the realm from `keycloak/realm.json` (Realm settings, Partial import) without the dev user,
and change the web client's redirect URIs to your frontend's address.

## 2. Add the customer's identity provider

For a SAML customer, in the Keycloak admin console:

1. **Identity providers → SAML v2.0.** Use an alias per customer, such as `acme-saml`.
2. Paste the customer's **IdP metadata URL** (Entra ID: Enterprise application → Single sign-on →
   App Federation Metadata Url) and save. Keycloak reads the endpoints and signing certificate.
3. Send the customer **your** metadata so they can register you:
   `https://auth.example.com/realms/ek-app-slug/broker/acme-saml/endpoint/descriptor`.
   They need the entity ID and the ACS URL in it.
4. Turn on **Want assertions signed** and **Validate signatures**. Set **Principal type** to the
   attribute that holds a stable id (Entra ID: `http://schemas.microsoft.com/identity/claims/objectidentifier`),
   not the email, which people change.
5. Under **Mappers**, map the email, first name and last name attributes.

For an OIDC customer (Okta, Google Workspace, Entra ID) choose **OpenID Connect v1.0** instead,
paste their discovery URL, client id and secret, and keep **PKCE** on.

## 3. Keep accounts from being taken over

A brokered sign-in can link to an existing account in your realm. Do this only when the customer's
provider is authoritative for the email's domain:

- Leave **Trust email** off unless the customer owns every address their provider issues. With it
  on, anyone who controls an account in that IdP with your user's email gets that user's account.
- Under the provider's **First login flow**, keep the default *first broker login*, which asks an
  existing user to prove the account (password or email link) before linking.
- Restrict each provider to the customer's domains with Keycloak **Organizations** (realm settings →
  Organizations → link the identity provider to the domain). Users from other domains cannot use it.

## 4. Give customer staff roles

The API reads roles from `realm_access.roles` (see `Features/Auth/Permissions.cs`). Map the
customer's groups to your realm roles with an **Advanced attribute to role** mapper on the identity
provider, for example SAML attribute `groups` containing `<their admin group id>` grants `auditor`.
Do not hand out `admin` from a customer's provider: it can read every customer's data.

## 5. Send users to the right provider

The frontend starts the sign-in at Keycloak's authorize endpoint. Add `kc_idp_hint=acme-saml` to
skip Keycloak's login page and go straight to the customer's provider, for example when the user
types an `@acme.com` address or visits `acme.yourapp.com`. With Organizations enabled, Keycloak can
also pick the provider from the email domain on its own login page.

## Check it

1. Sign in through the customer's provider and decode the access token (`jwt.io` or `jq`): `iss` is
   your realm, `aud` contains `ek-app-slug-api`, and `realm_access.roles` holds the mapped roles.
2. Call `GET /v1/notes` with the token: 200. Call `GET /v1/admin/notes`: 403 unless the user has a
   role with `notes.read.all`.
3. Remove the user in the customer's provider: they can no longer sign in, and their API access ends
   when the current access token expires (Keycloak's default is 5 minutes).
