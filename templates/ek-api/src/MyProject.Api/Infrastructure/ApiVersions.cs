using Asp.Versioning;
using Asp.Versioning.Builder;

namespace MyProject.Api.Infrastructure;

/// <summary>
/// The API's versions. A version changes only for a breaking change; anything that adds to a response
/// or accepts a new optional field ships in the current version. Each endpoint says which versions it
/// serves, so an unchanged endpoint keeps serving a new version without a copy.
/// </summary>
internal static class ApiVersions
{
    public static readonly ApiVersion V1 = new(1);

    /// <summary>The note list without the total count; see <c>NoteEndpoints</c>.</summary>
    public static readonly ApiVersion V2 = new(2);

    /// <summary>The OpenAPI documents, one per version, in the order the API reference lists them.</summary>
    public static readonly string[] Documents = ["v1", "v2"];

    /// <summary>The route prefix every versioned group starts with: <c>/v1</c>, <c>/v2</c>.</summary>
    public const string Prefix = "/v{version:apiVersion}";

    /// <summary>A group under <see cref="Prefix"/> that serves <paramref name="versions"/>.</summary>
    public static RouteGroupBuilder MapVersionedGroup(this IVersionedEndpointRouteBuilder api, params ApiVersion[] versions)
    {
        var group = api.MapGroup(Prefix);
        foreach (var version in versions)
        {
            group.HasApiVersion(version);
        }

        return group;
    }

    /// <summary>The path of <paramref name="relativePath"/> in the version this request asked for, for Location headers.</summary>
    public static string PathFor(HttpContext http, string relativePath) =>
        $"/v{(http.RequestedApiVersion ?? V1).MajorVersion}/{relativePath}";
}
