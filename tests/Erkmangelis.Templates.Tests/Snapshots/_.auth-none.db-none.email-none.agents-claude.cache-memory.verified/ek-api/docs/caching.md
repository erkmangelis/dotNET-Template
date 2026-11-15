# Caching

Two kinds of cache, both in each instance's memory and set up in
`src/Acme.Notes.Api/Infrastructure/Caching`:

- **HybridCache** keeps values your code asks for.
- **Output caching** keeps whole responses that are the same for everyone.

## HybridCache: values

`NoteCache` (`Features/Notes/NoteCache.cs`) is the example: `GET /v1/notes/{id}` reads through it,
and every save that changes a note removes its entry afterwards.

```csharp
var note = await cache.GetOrCreateAsync(
    $"notes:{id:N}",
    (store, id),
    static async (state, token) => await state.store.FindAsync(state.id, token),
    tags: ["notes"],
    cancellationToken: cancellationToken);
```

What HybridCache does for you:

- **One read per miss.** Callers that ask for the same key at once wait for one call to the source
  (no stampede when a popular entry expires).
- **Copies.** Values are stored serialized, so each caller gets its own copy and cannot change the
  cached one by accident. Mark a type `[ImmutableObject(true)]` (and make it truly immutable) to skip
  the copy.
- **Tags.** `RemoveByTagAsync("notes")` drops every entry with that tag.

Rules that keep cached data correct:

1. **Read through the cache only where a slightly old value is fine** (showing a note), never where
   a decision depends on it. Notes' writes read the store directly, so an edit's version check never
   runs against a cached copy.
2. **Remove the entry after every save** that changes, adds or deletes the data, as the note
   endpoints do with `NoteCache.ForgetAsync`. Keys must be built from ids, never from user text.
3. **Check access after the cache, not before.** The cache key has no user in it, so `GetAsync`
   checks ownership on the cached note, the same as on one from the database.
4. **"Not found" is cached too.** Creating a note removes its key for that reason.

### How fresh is it?

Entries live for `Caching:LocalExpirationSeconds` (30 seconds). A change removes the entry at once
on the instance that made it. With more than one instance, the others keep their copy until it
expires; choose `--cache redis` to share the cache between instances.
Values larger than `Caching:MaximumPayloadBytes` (1 MB) are never cached.

## Output caching: whole responses

An endpoint opts in with `.CacheOutput(CachePolicies.Shared)`; the response is kept for
`Caching:OutputExpirationSeconds` (60). The OpenAPI document is the example: building it reflects
over every endpoint, and it changes only on deploy.

The default rules never cache a response to a request that carries credentials (an `Authorization`
header or a cookie), or one that sets a cookie, and cache only `GET` and `HEAD` with status 200.
So output caching suits public, same-for-everyone responses; per-user data belongs in HybridCache
behind an access check. A response served from the cache carries an `Age` header.

To drop cached responses when data changes, tag the policy and evict the tag:

```csharp
options.AddPolicy("catalog", policy => policy.Tag("catalog").Expire(TimeSpan.FromMinutes(5)));
// After saving a change:
await outputCacheStore.EvictByTagAsync("catalog", cancellationToken);
```
