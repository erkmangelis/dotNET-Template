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

        notes.MapPost("/", CreateAsync).WithName("CreateNote").WithSummary("Create a note");
        notes.MapGet("/{id:guid}", GetAsync).WithName("GetNote").WithSummary("Get a note");
        notes.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateNote").WithSummary("Replace a note's title and content");
        notes.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteNote").WithSummary("Delete a note");
        notes.MapGet("/", ListAsync).WithName("ListNotes").WithSummary("List notes: filter by title or creation time, sort, page");

        return notes;
    }

    private static async Task<Results<Created<NoteResponse>, ProblemHttpResult>> CreateAsync(
        CreateNoteRequest request,
        INoteStore store,
        TimeProvider time,
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

        return TypedResults.Created($"/v1/notes/{note.Id}", NoteResponse.From(note));
    }

    private static async Task<Results<Ok<NoteResponse>, NotFound>> GetAsync(
        Guid id,
        INoteStore store,
        CancellationToken cancellationToken)
    {
        var note = await store.FindAsync(id, cancellationToken);
        return note is null ? TypedResults.NotFound() : TypedResults.Ok(NoteResponse.From(note));
    }

    private static async Task<Results<Ok<NoteResponse>, NotFound, ProblemHttpResult>> UpdateAsync(
        Guid id,
        UpdateNoteRequest request,
        INoteStore store,
        CancellationToken cancellationToken)
    {
        var note = await store.FindAsync(id, cancellationToken);
        if (note is null)
        {
            return TypedResults.NotFound();
        }

        note.Title = request.Title.Trim();
        note.Content = request.Content;
        note.Version = request.Version ?? note.Version;
        return await store.TryUpdateAsync(note, cancellationToken) switch
        {
            NoteSaveResult.Saved => TypedResults.Ok(NoteResponse.From(note)),
            NoteSaveResult.Stale => EditConflict(),
            _ => DuplicateTitle(),
        };
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> DeleteAsync(
        Guid id,
        INoteStore store,
        CancellationToken cancellationToken)
    {
        var note = await store.FindAsync(id, cancellationToken);
        if (note is null)
        {
            return TypedResults.NotFound();
        }

        return await store.TryDeleteAsync(note, cancellationToken) == NoteSaveResult.Saved
            ? TypedResults.NoContent()
            : EditConflict();
    }

    private static ProblemHttpResult EditConflict() => TypedResults.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Edit conflict",
        detail: "The note changed since you read it. Read it again and reapply your change.");

    private static ProblemHttpResult DuplicateTitle() => TypedResults.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Duplicate title",
        detail: "A note with this title already exists.");

    private static async Task<Ok<PagedResult<NoteResponse>>> ListAsync(
        [AsParameters] NoteListQuery query,
        INoteStore store,
        CancellationToken cancellationToken)
    {
        var notes = await store.ListAsync(query, cancellationToken);
        return TypedResults.Ok(notes.Map(NoteResponse.From));
    }
}
