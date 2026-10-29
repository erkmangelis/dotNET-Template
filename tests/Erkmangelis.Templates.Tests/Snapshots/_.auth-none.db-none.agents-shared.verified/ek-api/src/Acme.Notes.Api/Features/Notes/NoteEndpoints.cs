using System.ComponentModel.DataAnnotations;
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
        notes.MapGet("/", ListAsync).WithName("ListNotes").WithSummary("List notes, newest first");

        return notes;
    }

    private static async Task<Results<Created<NoteResponse>, ProblemHttpResult>> CreateAsync(
        CreateNoteRequest request,
        INoteStore store,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        // Whole microseconds, the precision databases usually store, so the response matches later reads.
        var utcNow = time.GetUtcNow();
        var now = utcNow.AddTicks(-(utcNow.Ticks % TimeSpan.TicksPerMicrosecond));
        var note = new Note
        {
            Id = Guid.CreateVersion7(now),
            Title = request.Title.Trim(),
            Content = request.Content,
            CreatedAt = now,
        };

        if (!await store.TryAddAsync(note, cancellationToken))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Duplicate title",
                detail: "A note with this title already exists.");
        }

        return TypedResults.Created($"/v1/notes/{note.Id}", NoteResponse.From(note));
    }

    private static async Task<Results<Ok<NoteResponse>, NotFound>> GetAsync(
        Guid id,
        INoteStore store,
        CancellationToken cancellationToken)
    {
        var note = await store.FindAsync(id, cancellationToken);
        if (note is null)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(NoteResponse.From(note));
    }

    private static async Task<Ok<PagedResult<NoteResponse>>> ListAsync(
        INoteStore store,
        CancellationToken cancellationToken,
        [Range(1, Paging.MaxPage)] int page = 1,
        [Range(1, Paging.MaxPageSize)] int pageSize = Paging.DefaultPageSize)
    {
        var notes = await store.ListAsync(page, pageSize, cancellationToken);
        return TypedResults.Ok(notes.Map(NoteResponse.From));
    }
}
