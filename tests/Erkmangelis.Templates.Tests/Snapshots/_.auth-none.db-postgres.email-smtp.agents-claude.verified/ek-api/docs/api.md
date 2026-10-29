# API conventions

How the endpoints behave, using Notes as the example. Follow the same rules when you add a feature.

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

## Concurrent edits

Every note has a `version` that changes on each save. Send the `version` you read with
`PUT /v1/notes/{id}`: when someone else changed the note in between, the update answers
409 ("Edit conflict") and changes nothing. Read the note again, reapply the change and retry.
Without a `version` the update still fails rather than overwrite a change that races it.

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
