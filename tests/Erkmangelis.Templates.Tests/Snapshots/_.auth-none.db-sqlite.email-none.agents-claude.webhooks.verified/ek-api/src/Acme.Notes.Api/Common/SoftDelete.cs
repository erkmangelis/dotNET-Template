namespace Acme.Notes.Api.Common;

/// <summary>
/// Entities that are marked deleted instead of removed, so they stay in the audit trail and an
/// operator can restore them. Deleting one sets these fields; queries leave it out from then on.
/// </summary>
internal interface ISoftDeletable
{
    DateTimeOffset? DeletedAt { get; set; }
}

internal static class SoftDelete
{
    /// <summary>The name of the query filter that hides deleted rows. Turn it off with <c>IgnoreQueryFilters([SoftDelete.Filter])</c>.</summary>
    public const string Filter = "SoftDelete";
}

/// <summary>Entities whose every change is written to the audit history. Set their keys before saving, for example with <see cref="Guid.CreateVersion7()"/>.</summary>
internal interface IAudited;
