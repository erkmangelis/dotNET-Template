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
        notes.MapPost("/", CreateAsync).RequireAuthorization(NotePolicies.Write).WithName("CreateNote").WithSummary("Create a note");
        notes.MapGet("/{id:guid}", GetAsync).RequireAuthorization(NotePolicies.Read).WithName("GetNote").WithSummary("Get a note");
        notes.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization(NotePolicies.Write).WithName("UpdateNote").WithSummary("Replace a note's title and content");
        notes.MapGet("/", ListAsync).RequireAuthorization(NotePolicies.Read).WithName("ListNotes").WithSummary("List notes: filter by title or creation time, sort, page");
#else
        notes.MapPost("/", CreateAsync).WithName("CreateNote").WithSummary("Create a note");
        notes.MapGet("/{id:guid}", GetAsync).WithName("GetNote").WithSummary("Get a note");
        notes.MapPut("/{id:guid}", UpdateAsync).WithName("UpdateNote").WithSummary("Replace a note's title and content");
        notes.MapGet("/", ListAsync).WithName("ListNotes").WithSummary("List notes: filter by title or creation time, sort, page");
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

        return TypedResults.Created($"/v1/notes/{note.Id}", NoteResponse.From(note));
    }

    private static async Task<Results<Ok<NoteResponse>, NotFound>> GetAsync(
        Guid id,
        INoteStore store,
#if (UseAuth)
        ClaimsPrincipal user,
        IAuthorizationService authorization,
#endif
        CancellationToken cancellationToken)
    {
        var note = await store.FindAsync(id, cancellationToken);
        if (note is null)
        {
            return TypedResults.NotFound();
        }

#if (UseAuth)
        // Another user's note answers 404, not 403, so ids cannot be probed.
        var access = await authorization.AuthorizeAsync(user, note, NotePolicies.Owner);
        if (!access.Succeeded)
        {
            return TypedResults.NotFound();
        }

#endif
        return TypedResults.Ok(NoteResponse.From(note));
    }

    private static async Task<Results<Ok<NoteResponse>, NotFound, ProblemHttpResult>> UpdateAsync(
        Guid id,
        UpdateNoteRequest request,
        INoteStore store,
#if (UseAuth)
        ClaimsPrincipal user,
        IAuthorizationService authorization,
#endif
        CancellationToken cancellationToken)
    {
        var note = await store.FindAsync(id, cancellationToken);
#if (UseAuth)
        if (note is null || !(await authorization.AuthorizeAsync(user, note, NotePolicies.Owner)).Succeeded)
#else
        if (note is null)
#endif
        {
            return TypedResults.NotFound();
        }

        note.Title = request.Title.Trim();
        note.Content = request.Content;
        return await store.TryUpdateAsync(note, cancellationToken) switch
        {
            NoteSaveResult.Saved => TypedResults.Ok(NoteResponse.From(note)),
            _ => DuplicateTitle(),
        };
    }

    private static ProblemHttpResult DuplicateTitle() => TypedResults.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: "Duplicate title",
        detail: "A note with this title already exists.");

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
