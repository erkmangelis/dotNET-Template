using Microsoft.AspNetCore.Http.HttpResults;

namespace Acme.Notes.Api.Features.Notes;

internal static class NoteEndpoints
{
    public static IServiceCollection AddNotes(this IServiceCollection services)
    {
        services.AddSingleton<INoteStore, InMemoryNoteStore>();
        return services;
    }

    public static RouteGroupBuilder MapNoteEndpoints(this IEndpointRouteBuilder routes)
    {
        var notes = routes.MapGroup("/notes").WithTags("Notes");

        notes.MapPost("/", CreateAsync).WithIdempotency().WithName("CreateNote").WithSummary("Create a note");
        notes.MapGet("/{id:guid}", GetAsync).WithName("GetNote").WithSummary("Get a note");
        notes.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateNote").WithSummary("Replace a note's title and content");
        notes.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteNote").WithSummary("Delete a note");
        notes.MapGet("/", ListAsync).MapToApiVersion(ApiVersions.V1).WithName("ListNotes").WithSummary("List notes: filter by title or creation time, sort, page");
        notes.MapGet("/", ListV2Async).MapToApiVersion(ApiVersions.V2).WithName("ListNotesV2").WithSummary("List notes: filter, sort, page, without the total count");
        notes.MapGet("/export", Export).WithName("ExportNotes").WithSummary("Stream every matching note as one JSON array");

        return notes;
    }

    private static async Task<Results<Created<NoteResponse>, ProblemHttpResult>> CreateAsync(
        CreateNoteRequest request,
        INoteStore store,
        TimeProvider time,
        HttpContext http,
        AppEvents events,
        CancellationToken cancellationToken)
    {
        // The store fills in the record metadata when it saves.
        var note = new Note
        {
            Id = Guid.CreateVersion7(time.GetUtcNow()),
            Title = request.Title.Trim(),
            Content = request.Content,
        };

        if (!await store.TryAddAsync(note, cancellationToken))
        {
            return DuplicateTitle();
        }

        http.Response.Headers.ETag = ETags.For(note.Version);
        await events.PublishAsync(NoteEvent(AppEventTypes.NoteCreated, note), cancellationToken);
        return TypedResults.Created(ApiVersions.PathFor(http, $"notes/{note.Id}"), NoteResponse.From(note));
    }

    private static async Task<Results<Ok<NoteResponse>, NotFound, StatusCodeHttpResult>> GetAsync(
        Guid id,
        INoteStore store,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var note = await store.FindAsync(id, cancellationToken);
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
        AppEvents events,
        CancellationToken cancellationToken)
    {
        var note = await store.FindAsync(id, cancellationToken);
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
            await events.PublishAsync(NoteEvent(AppEventTypes.NoteUpdated, note), cancellationToken);
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
        AppEvents events,
        CancellationToken cancellationToken)
    {
        var note = await store.FindAsync(id, cancellationToken);
        if (note is null)
        {
            return TypedResults.NotFound();
        }

        var ifMatch = ETags.CheckIfMatch(request, note.Version);
        if (ifMatch == IfMatch.Fails)
        {
            return PreconditionFailed();
        }

        var result = await store.TryDeleteAsync(note, cancellationToken);
        if (result == NoteSaveResult.Saved)
        {
            await events.PublishAsync(new AppEvent(AppEventTypes.NoteDeleted, UserId: null, new NoteDeletedEvent(note.Id)), cancellationToken);
        }

        return result switch
        {
            NoteSaveResult.Saved => TypedResults.NoContent(),
            _ when ifMatch == IfMatch.Matches => PreconditionFailed(),
            _ => EditConflict(),
        };
    }

    private static AppEvent NoteEvent(string type, Note note) => new(type, UserId: null, NoteResponse.From(note));

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
    private static Ok<IAsyncEnumerable<NoteResponse>> Export([AsParameters] NoteFilter filter, INoteStore store) =>
        TypedResults.Ok(store.StreamAsync(filter).Select(NoteResponse.From));

    /// <summary>Version 2 of the list: the same filters and page, but no total count, which costs a full scan on a large table.</summary>
    private static async Task<Ok<PageSlice<NoteResponse>>> ListV2Async(
        [AsParameters] NoteListQuery query,
        INoteStore store,
        CancellationToken cancellationToken)
    {
        var notes = await store.ListSliceAsync(query, cancellationToken);
        return TypedResults.Ok(notes.Map(NoteResponse.From));
    }

    private static async Task<Ok<PagedResult<NoteResponse>>> ListAsync(
        [AsParameters] NoteListQuery query,
        INoteStore store,
        CancellationToken cancellationToken)
    {
        var notes = await store.ListAsync(query, cancellationToken);
        return TypedResults.Ok(notes.Map(NoteResponse.From));
    }
}
