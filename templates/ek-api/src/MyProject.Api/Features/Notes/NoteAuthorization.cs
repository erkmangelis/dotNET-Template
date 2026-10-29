using Microsoft.AspNetCore.Authorization;

namespace MyProject.Api.Features.Notes;

internal static class NotePolicies
{
    /// <summary>Only the user who created a note may read it.</summary>
    public const string Owner = "notes.owner";

    /// <summary>Signed-in users, or API keys with <see cref="ApiKeyScopes.NotesRead"/>.</summary>
    public const string Read = "notes.read";

    /// <summary>Signed-in users, or API keys with <see cref="ApiKeyScopes.NotesWrite"/>.</summary>
    public const string Write = "notes.write";
}

internal sealed class NoteOwnerRequirement : IAuthorizationRequirement;

internal sealed class NoteOwnerHandler : AuthorizationHandler<NoteOwnerRequirement, Note>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, NoteOwnerRequirement requirement, Note resource)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && string.Equals(context.User.GetUserId(), resource.OwnerId, StringComparison.Ordinal))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
