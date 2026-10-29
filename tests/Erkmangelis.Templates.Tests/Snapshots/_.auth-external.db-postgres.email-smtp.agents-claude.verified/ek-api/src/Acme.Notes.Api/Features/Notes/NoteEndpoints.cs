using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Acme.Notes.Api.Features.Notes;

internal static class NoteEndpoints
{
    public static IServiceCollection AddNotes(this IServiceCollection services)
    {
        services.AddScoped<INoteStore, EfNoteStore>();
        services.AddSingleton<IAuthorizationHandler, NoteOwnerHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(NotePolicies.Owner, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new NoteOwnerRequirement()));
        services.AddUserOrKeyPolicy(NotePolicies.Read, ApiKeyScopes.NotesRead);
        services.AddUserOrKeyPolicy(NotePolicies.Write, ApiKeyScopes.NotesWrite);
        return services;
    }

    public static RouteGroupBuilder MapNoteEndpoints(this IEndpointRouteBuilder routes)
    {
        var notes = routes.MapGroup("/notes").WithTags("Notes");

        // Signed-in users, and API keys with the matching scope.
        notes.MapPost("/", CreateAsync).RequireAuthorization(NotePolicies.Write).WithName("CreateNote").WithSummary("Create a note");
        notes.MapGet("/{id:guid}", GetAsync).RequireAuthorization(NotePolicies.Read).WithName("GetNote").WithSummary("Get a note");
        notes.MapGet("/", ListAsync).RequireAuthorization(NotePolicies.Read).WithName("ListNotes").WithSummary("List notes: filter by title or creation time, sort, page");

        return notes;
    }

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

    private static async Task<Results<Created<NoteResponse>, ProblemHttpResult>> CreateAsync(
        CreateNoteRequest request,
        INoteStore store,
        TimeProvider time,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        // Whole microseconds, the precision databases usually store, so the response matches later reads.
        var utcNow = time.GetUtcNow();
        var now = utcNow.AddTicks(-(utcNow.Ticks % TimeSpan.TicksPerMicrosecond));
        var note = new Note
        {
            Id = Guid.CreateVersion7(now),
            OwnerId = user.GetUserId(),
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
        ClaimsPrincipal user,
        IAuthorizationService authorization,
        CancellationToken cancellationToken)
    {
        var note = await store.FindAsync(id, cancellationToken);
        if (note is null)
        {
            return TypedResults.NotFound();
        }

        // Another user's note answers 404, not 403, so ids cannot be probed.
        var access = await authorization.AuthorizeAsync(user, note, NotePolicies.Owner);
        if (!access.Succeeded)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(NoteResponse.From(note));
    }

    private static async Task<Ok<PagedResult<NoteResponse>>> ListAsync(
        [AsParameters] NoteListQuery query,
        INoteStore store,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var notes = await store.ListAsync(user.GetUserId(), query, cancellationToken);
        return TypedResults.Ok(notes.Map(NoteResponse.From));
    }

    private static async Task<Ok<PagedResult<AdminNoteResponse>>> ListAllAsync(
        [AsParameters] NoteListQuery query,
        INoteStore store,
        CancellationToken cancellationToken)
    {
        var notes = await store.ListAsync(ownerId: null, query, cancellationToken);
        return TypedResults.Ok(notes.Map(AdminNoteResponse.From));
    }
}
