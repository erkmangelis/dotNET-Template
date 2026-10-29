# API conventions

How the endpoints behave, using Notes as the example. Follow the same rules when you add a feature.

## Versions

The version is the first path segment: `/v1/notes`, `/v2/notes`. Each version has its own OpenAPI
document (`/openapi/v1.json`, `/openapi/v2.json`), and the API reference at `/scalar` lists both.
Every response names the versions its endpoint serves in `api-supported-versions`.

A new version exists only for a breaking change: removing or renaming a field, changing a type or a
status code. Adding a response field or an optional request field ships in the current version.
Version 2 changes one thing: `GET /v2/notes` drops `totalCount` and `totalPages`, because counting
every match costs a full scan on a large table, and keeps `hasNextPage`. Every other Notes endpoint
serves both versions unchanged, and the rest of the API is version 1 only.

To change an endpoint in a new version:

1. Add the version to `ApiVersions` and its document to `ApiVersions.Documents`.
2. Serve the feature's group in both versions (`api.MapVersionedGroup(ApiVersions.V1, ApiVersions.V3)`).
3. Map the old handler with `.MapToApiVersion(V1)` and the new one with `.MapToApiVersion(V3)`, each
   with its own `WithName`.

When a version is on its way out, mark it with `.HasDeprecatedApiVersion(...)` so responses list it in
`api-deprecated-versions`, tell clients the date, and remove it only after that date.

## Lists: paging, filtering and sorting

`GET /v1/notes` returns one page: `items`, `page`, `pageSize`, `totalCount`, `totalPages` and
`hasNextPage`. `page` starts at 1 and `pageSize` is at most 100.

| Parameter | Meaning |
| --- | --- |
| `search` | Titles that contain the text, ignoring case. `%` and `_` match themselves. |
| `createdFrom`, `createdBefore` | Notes created at or after, and before, a time such as `2026-10-29T12:00:00Z`. |
| `sort` | Up to three of `createdAt`, `updatedAt` and `title`, comma-separated, each with an optional `-` for descending. The default is `-createdAt`. |

Only allowlisted fields sort, so a client can never order by a column you did not choose; anything
else answers 400. Every sort ends with the id, so pages never overlap. For a new list, declare its
fields with `SortFields<T>` the way `NoteListQuery` does.

## Record metadata

Every note has `createdAt` and `updatedAt`. The server sets them on save; a client that sends them is
ignored.

## Concurrent edits and conditional requests

Every note has a `version` that changes on each save, and responses carry it as a strong `ETag`
(`"42"`). There are two ways to make sure an update or delete does not overwrite a newer change:

- Send the `ETag` you read in `If-Match`. When the note changed since, the request answers 412
  ("Precondition failed") and changes nothing. `If-Match` works on `PUT` and `DELETE`.
- Or send the `version` you read in the `PUT` body. A stale version answers 409 ("Edit conflict").

Either way, read the note again, reapply the change and retry. Without either, the request still
fails rather than overwrite a change that races it between the server's read and write.

`GET /v1/notes/{id}` with `If-None-Match: "<etag>"` answers 304 with no body while the note is
unchanged, so a client can keep a cached copy cheaply.

## Large results: streaming

`GET /v1/notes/export` takes the same `search`, `createdFrom`, `createdBefore` and `sort` as the list
and returns every match as one JSON array, written as rows are read from the database. Nothing is
paged and nothing is held in memory whole, so use it for exports and syncs instead of walking pages.

## Retries: idempotency keys

`POST /v1/notes` accepts an `Idempotency-Key` header, any unique value such as a UUID. Send the same
key when you retry after a timeout or a dropped connection:

| What happens | Answer |
| --- | --- |
| The first request finished | Its response again, with `Idempotent-Replayed: true`. Nothing runs twice. |
| The first request is still running | 409. Retry a little later. |
| The key was used for a different request | 422. Use a new key for a new request. |
| The first request failed with a 5xx | Nothing was kept, so the retry runs normally. |

Keys belong to the caller who sent them and are kept for 24 hours (`Idempotency:RetentionHours`).
To make another endpoint retry-safe, add `.WithIdempotency()` to it. PUT and DELETE need no key:
repeating them gives the same result.
