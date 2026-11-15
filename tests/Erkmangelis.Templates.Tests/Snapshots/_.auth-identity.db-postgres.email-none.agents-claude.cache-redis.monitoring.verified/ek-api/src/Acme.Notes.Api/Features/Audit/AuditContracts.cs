using System.Text.Json;

namespace Acme.Notes.Api.Features.Audit;

/// <summary>One change in a record's history. <c>Changes</c> maps each changed field to its value before and after.</summary>
public sealed record AuditEntryResponse(string Action, DateTimeOffset OccurredAt, string? ActorId, JsonElement Changes)
{
    internal static AuditEntryResponse From(AuditEntry entry) =>
        new(entry.Action.ToString(), entry.OccurredAt, entry.ActorId, Parse(entry.Changes));

    internal static JsonElement Parse(string changes)
    {
        using var document = JsonDocument.Parse(changes);
        return document.RootElement.Clone();
    }
}

/// <summary>An audit entry as an admin sees it, with the record it belongs to and the request's trace id.</summary>
public sealed record AdminAuditEntryResponse(
    Guid Id,
    string EntityType,
    string EntityId,
    string Action,
    DateTimeOffset OccurredAt,
    string? ActorId,
    string? TraceId,
    JsonElement Changes)
{
    internal static AdminAuditEntryResponse From(AuditEntry entry) => new(
        entry.Id,
        entry.EntityType,
        entry.EntityId,
        entry.Action.ToString(),
        entry.OccurredAt,
        entry.ActorId,
        entry.TraceId,
        AuditEntryResponse.Parse(entry.Changes));
}
