namespace Acme.Notes.Api.Common;

/// <summary>
/// Entities that are marked deleted instead of removed, so they stay in the audit trail and an
/// operator can restore them. Deleting one sets these fields; queries leave it out from then on.
/// </summary>
internal interface ISoftDeletable
{
    DateTimeOffset? DeletedAt { get; set; }
}
