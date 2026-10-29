#if (UsePostgres)
using System.ComponentModel.DataAnnotations;
#endif
#if (UseAuth)
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
#endif
using Microsoft.AspNetCore.Http.HttpResults;

namespace MyProject.Api.Features.Notes;

internal static class NoteEndpoints
{
    public static IServiceCollection AddNotes(this IServiceCollection services)
    {
#if (UsePostgres)
        services.AddScoped<INoteStore, EfNoteStore>();
#else
        services.AddSingleton<INoteStore, InMemoryNoteStore>();
#endif
#if (UseAuth)
        services.AddSingleton<IAuthorizationHandler, NoteOwnerHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(NotePolicies.Owner, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new NoteOwnerRequirement()));
        services.AddUserOrKeyPolicy(NotePolicies.Read, ApiKeyScopes.NotesRead);
        services.AddUserOrKeyPolicy(NotePolicies.Write, ApiKeyScopes.NotesWrite);
#endif
        return services;
    }

    public static RouteGroupBuilder MapNoteEndpoints(this IEndpointRouteBuilder routes)
    {
        var notes = routes.MapGroup("/notes").WithTags("Notes");

#if (UseAuth)
        // Signed-in users, and API keys with the matching scope.
        notes.MapPost("/", CreateAsync).RequireAuthorization(NotePolicies.Write).WithIdempotency().WithName("CreateNote").WithSummary("Create a note");
        notes.MapGet("/{id:guid}", GetAsync).RequireAuthorization(NotePolicies.Read).WithName("GetNote").WithSummary("Get a note");
        notes.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization(NotePolicies.Write).WithName("UpdateNote").WithSummary("Replace a note's title and content");
        notes.MapDelete("/{id:guid}", DeleteAsync).RequireAuthorization(NotePolicies.Write).WithName("DeleteNote").WithSummary("Delete a note");
#if (UsePostgres)
        notes.MapGet("/{id:guid}/history", HistoryAsync).RequireAuthorization(NotePolicies.Read).WithName("GetNoteHistory").WithSummary("List a note's changes, newest first");
#endif
        notes.MapGet("/", ListAsync).RequireAuthorization(NotePolicies.Read).WithName("ListNotes").WithSummary("List notes: filter by title or creation time, sort, page");
        notes.MapGet("/export", Export).RequireAuthorization(NotePolicies.Read).WithName("ExportNotes").WithSummary("Stream every matching note as one JSON array");
#else
        notes.MapPost("/", CreateAsync).WithIdempotency().WithName("CreateNote").WithSummary("Create a note");
        notes.MapGet("/{id:guid}", GetAsync).WithName("GetNote").WithSummary("Get a note");
        notes.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateNote").WithSummary("Replace a note's title and content");
        notes.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteNote").WithSummary("Delete a note");
#if (UsePostgres)
        notes.MapGet("/{id:guid}/history", HistoryAsync).WithName("GetNoteHistory").WithSummary("List a note's changes, newest first");
#endif
        notes.MapGet("/", ListAsync).WithName("ListNotes").WithSummary("List notes: filter by title or creation time, sort, page");
        notes.MapGet("/export", Export).WithName("ExportNotes").WithSummary("Stream every matching note as one JSON array");
#endif

        return notes;
    }
#if (UseAuth)

    /// <summary>Every user's notes, for roles with <see cref="Permissions.NotesReadAll"/>.</summary>
    public static RouteGroupBuilder MapNoteAdminEndpoints(this IEndpointRouteBuilder admin)
    {
        var notes = admin.MapGroup("/notes");
        notes.MapGet("/", ListAllAsync)
            .RequirePermission(Permissions.NotesReadAll)
            .WithName("ListAllNotes")
            .WithSummary("List every user's notes: filter, sort, page");

        return notes;
    }
#endif

    private static async Task<Results<Created<NoteResponse>, ProblemHttpResult>> CreateAsync(
        CreateNoteRequest request,
        INoteStore store,
        TimeProvider time,
        HttpResponse response,
#if (UseAuth)
        ClaimsPrincipal user,
#endif
        CancellationToken cancellationToken)
    {
        // The store fills in the record metadata when it saves.
        var note = new Note
        {
            Id = Guid.CreateVersion7(time.GetUtcNow()),
#if (UseAuth)
            OwnerId = user.GetUserId(),
#endif
            Title = request.Title.Trim(),
            Content = request.Content,
        };

        if (!await store.TryAddAsync(note, cancellationToken))
        {
            return DuplicateTitle();
        }

        response.Headers.ETag = ETags.For(note.Version);
        return TypedResults.Created($"/v1/notes/{note.Id}", NoteResponse.From(note));
    }

    private static async Task<Results<Ok<NoteResponse>, NotFound, StatusCodeHttpResult>> GetAsync(
        Guid id,
        INoteStore store,
        HttpContext http,
#if (UseAuth)
        ClaimsPrincipal user,
        IAuthorizationService authorization,
#endif
        CancellationToken cancellationToken)
    {
#if (UseAuth)
        var note = await FindOwnAsync(id, store, user, authorization, cancellationToken);
#else
        var note = await store.FindAsync(id, cancellationToken);
#endif
        if (note is null)
        {
            return TypedResults.NotFound();
        }

        http.Response.Headers.ETag = ETags.For(note.Version);
        return ETags.IsNotModified(http.Request, note.Version)
            ? TypedResults.StatusCode(StatusCodes.Status304NotModified)
            : TypedResults.Ok(NoteResponse.From(note));
    }

    private static async Task<Results<Ok<NoteResponse>, NotFound, ProblemHttpResult>> UpdateAsync(
        Guid id,
        UpdateNoteRequest request,
        INoteStore store,
        HttpContext http,
#if (UseAuth)
        ClaimsPrincipal user,
        IAuthorizationService authorization,
#endif
        CancellationToken cancellationToken)
    {
#if (UseAuth)
        var note = await FindOwnAsync(id, store, user, authorization, cancellationToken);
#else
        var note = await store.FindAsync(id, cancellationToken);
#endif
        if (note is null)
        {
            return TypedResults.NotFound();
        }

        // If-Match is checked against the version just read; the save checks it again against the row.
        var ifMatch = ETags.CheckIfMatch(http.Request, note.Version);
        if (ifMatch == IfMatch.Fails)
        {
            return PreconditionFailed();
        }

        note.Title = request.Title.Trim();
        note.Content = request.Content;
        if (ifMatch == IfMatch.Absent)
        {
            note.Version = request.Version ?? note.Version;
        }

        var result = await store.TryUpdateAsync(note, cancellationToken);
        if (result == NoteSaveResult.Saved)
        {
            http.Response.Headers.ETag = ETags.For(note.Version);
        }

        return result switch
        {
            NoteSaveResult.Saved => TypedResults.Ok(NoteResponse.From(note)),
            NoteSaveResult.Stale when ifMatch == IfMatch.Matches => PreconditionFailed(),
            NoteSaveResult.Stale => EditConflict(),
            _ => DuplicateTitle(),
        };
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> DeleteAsync(
        Guid id,
        INoteStore store,
        HttpRequest request,
#if (UseAuth)
        ClaimsPrincipal user,
        IAuthorizationService authorization,
#endif
        CancellationToken cancellationToken)
    {
#if (UseAuth)
        var note = await FindOwnAsync(id, store, user, authorization, cancellationToken);
#else
        var note = await store.FindAsync(id, cancellationToken);
#endif
        if (note is null)
        {
            return TypedResults.NotFound();
        }

        var ifMatch = ETags.CheckIfMatch(request, note.Version);
        if (ifMatch == IfMatch.Fails)
        {
            return PreconditionFailed();
        }

        return await store.TryDeleteAsync(note, cancellationToken) switch
        {
            NoteSaveResult.Saved => TypedResults.NoContent(),
            _ when ifMatch == IfMatch.Matches => PreconditionFailed(),
            _ => EditConflict(),
        };
    }
#if (UsePostgres)

    private static async Task<Results<Ok<PagedResult<AuditEntryResponse>>, NotFound>> HistoryAsync(
        Guid id,
        INoteStore store,
        AuditLog log,
#if (UseAuth)
        ClaimsPrincipal user,
        IAuthorizationService authorization,
#endif
        CancellationToken cancellationToken,
        [Range(1, Paging.MaxPage)] int page = 1,
        [Range(1, Paging.MaxPageSize)] int pageSize = Paging.DefaultPageSize)
    {
#if (UseAuth)
        var note = await FindOwnAsync(id, store, user, authorization, cancellationToken);
#else
        var note = await store.FindAsync(id, cancellationToken);
#endif
        if (note is null)
        {
            return TypedResults.NotFound();
        }

#if (UseAuth)
        var entries = await log.ListAsync(nameof(Note), note.Id.ToString(), actorId: null, page, pageSize, cancellationToken);
#else
        var entries = await log.ListAsync(nameof(Note), note.Id.ToString(), page, pageSize, cancellationToken);
#endif
        return TypedResults.Ok(entries.Map(AuditEntryResponse.From));
    }
#endif
#if (UseAuth)

    /// <summary>The note, when it exists and <paramref name="user"/> owns it. Another user's note is not found, so ids cannot be probed.</summary>
    private static async Task<Note?> FindOwnAsync(
        Guid id,
        INoteStore store,
        ClaimsPrincipal user,
        IAuthorizationService authorization,
        CancellationToken cancellationToken)
    {
        var note = await store.FindAsync(id, cancellationToken);
        return note is not null && (await authorization.AuthorizeAsync(user, note, NotePolicies.Owner)).Succeeded ? note : null;
    }
#endif

    private static ProblemHttpResult PreconditionFailed() => TypedResults.Problem(
        statusCode: StatusCodes.Status412PreconditionFailed,
        title: "Precondition failed",
        detail: "If-Match does not name the note's current version. Read it again and reapply your change.");

    private static ProblemHttpResult EditConflict() => TypedResults.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Edit conflict",
        detail: "The note changed since you read it. Read it again and reapply your change.");

    private static ProblemHttpResult DuplicateTitle() => TypedResults.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Duplicate title",
        detail: "A note with this title already exists.");

    /// <summary>Streams the notes as they are read, so a large export never sits in memory whole.</summary>
#if (UseAuth)
    private static Ok<IAsyncEnumerable<NoteResponse>> Export([AsParameters] NoteFilter filter, INoteStore store, ClaimsPrincipal user) =>
        TypedResults.Ok(store.StreamAsync(user.GetUserId(), filter).Select(NoteResponse.From));
#else
    private static Ok<IAsyncEnumerable<NoteResponse>> Export([AsParameters] NoteFilter filter, INoteStore store) =>
        TypedResults.Ok(store.StreamAsync(filter).Select(NoteResponse.From));
#endif

    private static async Task<Ok<PagedResult<NoteResponse>>> ListAsync(
        [AsParameters] NoteListQuery query,
        INoteStore store,
#if (UseAuth)
        ClaimsPrincipal user,
#endif
        CancellationToken cancellationToken)
    {
#if (UseAuth)
        var notes = await store.ListAsync(user.GetUserId(), query, cancellationToken);
#else
        var notes = await store.ListAsync(query, cancellationToken);
#endif
        return TypedResults.Ok(notes.Map(NoteResponse.From));
    }
#if (UseAuth)

    private static async Task<Ok<PagedResult<AdminNoteResponse>>> ListAllAsync(
        [AsParameters] NoteListQuery query,
        INoteStore store,
        CancellationToken cancellationToken)
    {
        var notes = await store.ListAsync(ownerId: null, query, cancellationToken);
        return TypedResults.Ok(notes.Map(AdminNoteResponse.From));
    }
#endif
}
