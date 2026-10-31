# R03, Data and API depth (0.3.0): why the data layer works like this

R01 could store a note. R03 makes the data layer behave like a production system's: lists can be
filtered and sorted safely, every record says who changed it and when, deletes are reversible,
every change is audited, two people editing the same note cannot overwrite each other, retried
requests do not create duplicates, big exports stream, and the API can evolve with versions. It
also added two more databases (`--db sqlserver`, `--db sqlite`) and a recipe for hand-written SQL.

The common thread: **put rules where they cannot be forgotten.** Most patterns here move a rule out
of individual endpoints into one central place.

## Contents

1. [Filtering and sorting with an allowlist](#1-filtering-and-sorting-with-an-allowlist)
2. [Record metadata and soft delete in a `SaveChangesInterceptor`](#2-record-metadata-and-soft-delete-in-a-savechangesinterceptor)
3. [Audit history in the same transaction](#3-audit-history-in-the-same-transaction)
4. [Optimistic concurrency with row versions](#4-optimistic-concurrency-with-row-versions)
5. [ETags and conditional requests](#5-etags-and-conditional-requests)
6. [Idempotency keys](#6-idempotency-keys)
7. [Streaming with `IAsyncEnumerable`](#7-streaming-with-iasyncenumerable)
8. [API versioning in the URL](#8-api-versioning-in-the-url)
9. [Three database providers](#9-three-database-providers)
10. [Migrations for every combination](#10-migrations-for-every-combination)
11. [Dapper as a recipe, not a dependency](#11-dapper-as-a-recipe-not-a-dependency)

---

## 1. Filtering and sorting with an allowlist

```csharp
// templates/ek-api/src/MyProject.Api/Features/Notes/NoteContracts.cs
internal static readonly SortFields<Note> Sorts = new SortFields<Note>()
    .Add("createdAt", note => note.CreatedAt)
    .Add("updatedAt", note => note.UpdatedAt)
    .Add("title", note => note.Title);

internal IQueryable<Note> Order(IQueryable<Note> notes) =>
    Sorts.Apply(notes, Sort ?? DefaultSort).ThenByDescending(note => note.Id);
```

**The choice.** Clients sort with `?sort=-createdAt,title`, but only by fields the feature lists.
Anything else answers 400.

**Why an allowlist.** The tempting shortcut, "sort by any property name the client sends" (with
reflection or `System.Linq.Dynamic`), lets clients sort by columns that are not indexed (a slow
query on demand, which is a cheap denial of service) or by fields they should not see (sorting by a
hidden field leaks its order). Limiting to three terms bounds the database work.

**Why always end with the id.** If two notes have the same `createdAt`, the database may return
them in a different order on each query, and paging then skips or repeats rows. A unique last sort
key makes the order stable.

**Search escapes wildcards.** `search=50%` must find titles containing "50%", not everything
starting with "50". Each store escapes `%` and `_` for its database before building `LIKE`.

**Alternatives.** OData or GraphQL give clients a full query language. That is powerful and much
harder to secure and keep fast. Gridify and Sieve are libraries for the allowlist approach; the
template's `SortFields<T>` is about 80 lines, so a dependency was not worth it.

## 2. Record metadata and soft delete in a `SaveChangesInterceptor`

```csharp
// templates/ek-api/src/MyProject.Api/Data/AuditableEntityInterceptor.cs
/// Runs before every save, so no endpoint can forget it and no client can set what it sets:
/// turns deletes of ISoftDeletable entities into updates, fills in IRecordMetadata, and writes an
/// AuditEntry for each change to an IAudited entity.
if (entry is { State: EntityState.Deleted, Entity: ISoftDeletable deletable })
{
    entry.State = EntityState.Modified;
    deletable.DeletedAt = now;
    deletable.DeletedBy = actorId;
}
if (entry is { State: EntityState.Added or EntityState.Modified, Entity: IRecordMetadata metadata })
{
    RecordMetadata.Stamp(metadata, entry.State == EntityState.Added, now, actorId);
}
```

**What an interceptor is.** EF Core lets you hook into its pipeline. A `SaveChangesInterceptor` runs
on every `SaveChanges`, sees every tracked entity, and can change them before the SQL is generated.

**Why here and not in endpoints.** If each endpoint set `UpdatedAt = now`, one would eventually
forget. If the request contract had `UpdatedAt`, a client could set it. In the interceptor it
happens on every save, from `TimeProvider` and the signed-in user (`ICurrentActor`), never from the
request.

**Why soft delete.** A `DELETE` sets `DeletedAt` instead of removing the row. Users ask for
"undelete", support needs to see what happened, and the audit history keeps pointing at something.
Endpoint code still calls `Remove(note)` like normal EF code; the interceptor converts it.

**Hiding deleted rows: named query filters.**

```csharp
// templates/ek-api/src/MyProject.Api/Data/NoteConfiguration.cs
builder.HasQueryFilter(SoftDelete.Filter, note => note.DeletedAt == null);
// Unique among notes that are not deleted, so a deleted note's title can be used again.
builder.HasIndex(note => new { note.OwnerId, note.Title }).IsUnique().HasFilter(NotDeleted);
```

A global query filter adds `WHERE DeletedAt IS NULL` to every query on notes. EF Core 10 lets you
*name* filters, so an admin view can turn off just the soft-delete filter
(`IgnoreQueryFilters([SoftDelete.Filter])`) without turning off others (like tenant filters later
in R10).

The **filtered unique index** matters: without `HasFilter`, a deleted note called "Groceries"
would block creating a new "Groceries" forever.

**Trade-offs.** Soft-deleted rows take space and every query pays for the filter. R04 adds a
recurring job that purges notes deleted more than 30 days ago. Soft delete is also not erasure: for
GDPR "right to be forgotten" you still need a hard delete path.

**Microsecond precision.** `RecordMetadata.Now` trims the time to whole microseconds, because
PostgreSQL stores microseconds and .NET has 100-nanosecond ticks. Without it, the `createdAt` in the
create response would differ from the one a later read returns.

## 3. Audit history in the same transaction

Every change to an `IAudited` entity writes an `AuditEntry` with each changed property's old and
new value, the actor and the trace id. Owners see `GET /v1/notes/{id}/history`; auditors see
`GET /v1/admin/audit` (the `audit.read` permission from R02).

**Why in the same transaction.** The interceptor adds audit rows to the same `SaveChanges`, so the
change and its audit entry commit together or not at all. An audit written by a separate call
afterwards can be lost if the app crashes in between, and an audit trail with gaps is not one.

**Why the trace id.** From an audit row you can jump to the request's logs and traces (R01).

**Alternatives.** Database triggers (complete, but they do not know the app's user), temporal
tables (SQL Server and MariaDB have them; PostgreSQL does not natively), event sourcing (the history
*is* the data; a different architecture altogether). The interceptor works the same on all three
providers.

## 4. Optimistic concurrency with row versions

**The problem.** Alice and Bob both open note 7 (version 3). Alice saves. Bob saves a minute later
and silently overwrites Alice's change. This is the "lost update".

**The choice.** Each note has a `Version` that the database changes on every save. An update sends
the version it read, and the save's `UPDATE ... WHERE Id = 7 AND Version = 3` matches nothing if
someone else saved in between. EF Core then throws `DbUpdateConcurrencyException`, which the
store turns into `NoteSaveResult.Stale`, and the API answers **409 Edit conflict**.

```csharp
// NoteConfiguration.cs
#if (UseSqlite)
// SQLite has no row version, so EfNoteStore increments this on every save.
builder.Property(note => note.Version).IsConcurrencyToken();
#else
builder.Property(note => note.Version).IsRowVersion();
#endif
```

**How each database does it.**

| Provider | Version source | .NET type |
| --- | --- | --- |
| PostgreSQL | the system column `xmin` (transaction id of the last write), mapped by Npgsql | `uint` |
| SQL Server | a `rowversion` column, set automatically | `ulong` (mapped from 8 bytes) |
| SQLite | an ordinary column the store increments | `ulong` |

**Why optimistic, not pessimistic.** Pessimistic locking (lock the row while someone edits) blocks
others and leaves locks behind when a browser tab closes. Optimistic concurrency assumes conflicts
are rare, costs nothing until one happens, and then tells the client clearly.

## 5. ETags and conditional requests

The note's version is also its **ETag** (an HTTP header that identifies a version of a resource).

- `GET` with `If-None-Match: "3"` answers **304 Not Modified** with no body if the note is still at
  version 3. Clients and caches save bandwidth.
- `PUT` or `DELETE` with `If-Match: "3"` answers **412 Precondition Failed** if the note changed.
  This is the same protection as section 4, through a standard HTTP header instead of a body field.

```csharp
// templates/ek-api/src/MyProject.Api/Common/ETags.cs
public static string For(ulong version) => $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";
```

**Why both a body `version` and `If-Match`.** HTTP-aware clients and proxies understand headers;
simple clients find a JSON field easier. Supporting both costs little because they share one check.

**Strong vs weak comparison.** `If-Match` uses strong comparison (byte-identical versions only),
`If-None-Match` weak, as RFC 9110 requires. It is a detail, but getting it wrong breaks caches.

## 6. Idempotency keys

**The problem.** A mobile client sends `POST /v1/notes`, the network drops before the response
arrives, and the client retries. Did the first request succeed? Without help, retrying creates a
duplicate; not retrying may lose the note.

**The choice.** Endpoints marked `.WithIdempotency()` honour an `Idempotency-Key` header (the
pattern Stripe made popular, now an IETF draft):

```csharp
// templates/ek-api/src/MyProject.Api/Infrastructure/Idempotency/IdempotencyMiddleware.cs
/// A request ... that carries an Idempotency-Key runs once; a retry with the same key and the same
/// request gets the first response again, with Idempotent-Replayed: true. The same key with a
/// different request answers 422, and a retry while the first request is still running answers 409.
/// Server errors are not kept, so the client can retry them. Keys belong to the caller who sent them.
```

Each rule closes a hole:

- **Hash of the request.** Reusing a key with a *different* body is a client bug; replaying the old
  response would hide it, so 422.
- **Reserve before running.** The key row is inserted first. A parallel retry finds it and gets 409
  instead of running the work twice.
- **5xx not kept.** A server error might be temporary; the client should be able to retry.
- **Scoped per caller.** Otherwise one user could guess another's key and read their response.
- **Middleware after authentication and rate limiting**, so the key belongs to a known caller and a
  rejected request stores nothing.

Keys are kept for `Idempotency:RetentionHours` and cleaned up by a background loop (R04 moved it to
the `ScopedBackgroundLoop`).

**Alternatives.** Making the operation naturally idempotent (`PUT` to a client-chosen id) works
for some resources but changes the API's shape. Deduplicating by content (same title within a
minute) guesses intent and is wrong sometimes.

## 7. Streaming with `IAsyncEnumerable`

```csharp
// EfNoteStore
public IAsyncEnumerable<Note> StreamAsync(string ownerId, NoteFilter filter) => ...AsAsyncEnumerable();
```

`GET /v1/notes/export` returns every matching note as one JSON array. ASP.NET Core serializes an
`IAsyncEnumerable<T>` item by item as EF reads rows, so memory stays flat whether there are ten
notes or ten million. Loading everything with `ToListAsync()` first would hold it all in memory and
delay the first byte until the last row is read.

**Trade-off.** Once streaming has started, the status code (200) is already sent. An error halfway
cannot become a 500; the response just ends. Clients should check that the JSON is complete.

## 8. API versioning in the URL

**The choice.** `Asp.Versioning` (MIT, maintained by the .NET Foundation) with the version in the
path: `/v1/notes`, `/v2/notes`. Notes serve both; `v2`'s list drops the total count (`PageSlice<T>`),
everything else is shared.

```csharp
// Program.cs
api.MapVersionedGroup(ApiVersions.V1, ApiVersions.V2).MapNoteEndpoints();
notes.MapGet("/", ListAsync).MapToApiVersion(ApiVersions.V1);
notes.MapGet("/", ListV2Async).MapToApiVersion(ApiVersions.V2);
```

**Why a URL segment, not a header or query string.** It is visible in logs, works in a browser and
with every HTTP client, and caches treat versions as different URLs automatically. Header
versioning is "purer" REST, but harder to debug and easy for clients to forget.

**Why v2 drops the total count.** It is a real example of a breaking change worth a version:
`COUNT(*)` over a large table is expensive, and most UIs only need "is there a next page?". It shows
the mechanism without inventing a toy change.

**One OpenAPI document per version** (`/openapi/v1.json`, `/openapi/v2.json`), both in Scalar, and
`api-supported-versions` on responses so clients can discover newer versions.

## 9. Three database providers

| `--db` | When to pick it |
| --- | --- |
| `postgres` | The default for anything serious. Free, fast, runs everywhere. |
| `sqlserver` | Your company already runs SQL Server, or you need its tooling. |
| `sqlite` | One small machine, light write load, no database server to run (a Raspberry Pi). |

**Why not "just abstract the database".** EF Core hides most differences, but not all, and the
template is honest about the ones it hits:

- **Concurrency tokens** differ (section 4).
- **SQLite has no `DateTimeOffset` type.** Stored as text, it sorts wrongly across time zones, so
  the SQLite build stores UTC ticks (a number) through `ConfigureConventions`, which sort correctly.
- **SQL Server** clusters tables by primary key; random keys fragment that. The idempotency key
  index is `IsClustered(false)` for that reason.
- **SQL Server licence.** Compose runs the Express edition, and the user accepts Microsoft's licence
  by setting `MSSQL_ACCEPT_EULA` in `.env`. The template never accepts a licence on your behalf.
  The SQL Server image is amd64 only, so it will not run on a Pi.
- **Backups** differ: `pg_dump`, `BACKUP DATABASE`, and SQLite's online backup API (copying the file
  while the app writes to it can produce a corrupt copy).

**A vulnerability pin.** `Microsoft.Data.SqlClient` 6.1 pulls in `System.Security.Cryptography.Pkcs`
9.0.11, which has a known vulnerability. Because CI builds with warnings as errors, NuGet's audit
warning (NU1903) fails the build, which is exactly the point. The template pins the fixed 10.0.14
centrally. This is the supply-chain setup from R01 doing its job.

**Testcontainers for PostgreSQL and SQL Server, a temporary file for SQLite.** Tests run against
the real engine every time (R01, section 13).

## 10. Migrations for every combination

Migrations depend on both the database provider and the auth mode (Identity adds tables). Three
providers times three account shapes (no auth, Identity, OIDC) is nine migration sets, kept in
`templates/ek-api/.migrations/<provider>/{notes,identity,oidc}`, and `template.json` copies the
right one.

`scripts/add-migration.sh <Name>` generates each combination from the template, runs
`dotnet ef migrations add` in it, and copies the result back. A maintainer never writes nine
migrations by hand, and they never drift.

**Why not generate migrations at project creation.** It would need the EF tool and a build during
`dotnet new`, which the template engine cannot do, and the user would get a different migration
from the one CI tested.

**A gotcha worth knowing.** The template engine fails to load if a source folder named in
`template.json` does not exist, so every set folder must exist even if empty.

## 11. Dapper as a recipe, not a dependency

**The choice.** `docs/dapper.md` in generated projects shows how to write a hot read path in raw SQL
with Dapper, for each provider, keeping the soft-delete and owner filters. No Dapper package is
added by default.

**Why.** EF Core is fast enough for nearly everything, and every query through EF gets the
interceptor, the query filters and the owner checks for free. Raw SQL is for the few paths where
profiling says it matters, and it is exactly where people forget `WHERE DeletedAt IS NULL`. A
recipe teaches the pattern without adding a dependency nobody uses, and a test checks the recipe's
SQL returns the same rows as EF.

**A detail the recipe handles.** Npgsql reads `timestamptz` as `DateTime`, not `DateTimeOffset`, so
the recipe reads into a row record and converts.

---

## Try it

1. Create a note, then `PUT` it twice with the same `version`. The second answers 409.
2. `POST /v1/notes` twice with the same `Idempotency-Key`. Check for `Idempotent-Replayed: true`
   and that only one note exists. Change the title and retry with the same key: 422.
3. `GET` a note, copy its `ETag`, then `GET` again with `If-None-Match`. You get 304.
4. Delete a note, then look at the `Notes` table directly. The row is there, with `DeletedAt` set.
5. Try `?sort=content`. Read the 400 message.

## Further reading

- EF Core interceptors: https://learn.microsoft.com/ef/core/logging-events-diagnostics/interceptors
- EF Core concurrency: https://learn.microsoft.com/ef/core/saving/concurrency
- HTTP conditional requests (RFC 9110 section 13): https://www.rfc-editor.org/rfc/rfc9110#section-13
- The Idempotency-Key header draft: https://datatracker.ietf.org/doc/draft-ietf-httpapi-idempotency-key-header/
