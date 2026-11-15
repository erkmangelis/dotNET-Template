using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Acme.Notes.Api.UnitTests.Features.Notes;

public sealed class NoteOwnerHandlerTests
{
    private static readonly Note _alicesNote = new()
    {
        Id = Guid.CreateVersion7(),
        OwnerId = "alice",
        Title = "Private",
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task The_owner_is_allowed()
    {
        var context = await AuthorizeAsync(SignedIn("alice"));

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task Another_user_is_refused()
    {
        var context = await AuthorizeAsync(SignedIn("bob"));

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        var context = await AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.False(context.HasSucceeded);
    }

    private static async Task<AuthorizationHandlerContext> AuthorizeAsync(ClaimsPrincipal user)
    {
        var context = new AuthorizationHandlerContext([new NoteOwnerRequirement()], user, _alicesNote);
        await new NoteOwnerHandler().HandleAsync(context);
        return context;
    }

    private static ClaimsPrincipal SignedIn(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], authenticationType: "Test"));
}
