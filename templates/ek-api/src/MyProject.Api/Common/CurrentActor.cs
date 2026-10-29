using System.Security.Claims;

namespace MyProject.Api.Common;

/// <summary>Who is making the current change: the signed-in user's id, or null outside a request (startup, background work).</summary>
internal interface ICurrentActor
{
    string? Id { get; }
}

internal sealed class HttpCurrentActor(IHttpContextAccessor accessor) : ICurrentActor
{
    public string? Id => accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
}
