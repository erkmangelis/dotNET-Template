using System.Globalization;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Acme.Notes.Api.Common;

internal enum IfMatch
{
    /// <summary>The request has no <c>If-Match</c> header.</summary>
    Absent,

    /// <summary><c>If-Match</c> names the current version, or is <c>*</c>.</summary>
    Matches,

    /// <summary><c>If-Match</c> names only other versions: answer 412.</summary>
    Fails,
}

/// <summary>
/// Entity tags built from a row version, for conditional requests: <c>If-None-Match</c> on reads saves
/// downloading an unchanged resource, and <c>If-Match</c> on writes refuses to overwrite a newer one.
/// </summary>
internal static class ETags
{
    public static string For(ulong version) => $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    /// <summary>Whether <c>If-None-Match</c> already names <paramref name="version"/>, so the answer can be 304.</summary>
    public static bool IsNotModified(HttpRequest request, ulong version) =>
        Parse(request.Headers.IfNoneMatch) is { } tags
        && tags.Any(tag => tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(new EntityTagHeaderValue(For(version)), useStrongComparison: false));

    public static IfMatch CheckIfMatch(HttpRequest request, ulong version)
    {
        if (request.Headers.IfMatch.Count == 0)
        {
            return IfMatch.Absent;
        }

        // Unparseable values name no version of ours, so they fail like any other mismatch.
        return Parse(request.Headers.IfMatch) is { } tags
            && tags.Any(tag => tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(new EntityTagHeaderValue(For(version)), useStrongComparison: true))
            ? IfMatch.Matches
            : IfMatch.Fails;
    }

    private static IList<EntityTagHeaderValue>? Parse(StringValues values) =>
        values.Count > 0 && EntityTagHeaderValue.TryParseList(values, out var tags) ? tags : null;
}
