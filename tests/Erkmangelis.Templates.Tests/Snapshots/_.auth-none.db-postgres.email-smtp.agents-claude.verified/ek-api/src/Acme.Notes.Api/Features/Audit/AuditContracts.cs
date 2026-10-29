using System.Text.Json;

namespace Acme.Notes.Api.Features.Audit;

/// <summary>One change in a record's history. <c>Changes</c> maps each changed field to its value before and after.</summary>
public sealed record AuditEntryResponse(string Action, DateTimeOffset OccurredAt, JsonElement Changes)
{
    internal static AuditEntryResponse From(AuditEntry entry) =>
        new(entry.Action.ToString(), entry.OccurredAt, Parse(entry.Changes));

    internal static JsonElement Parse(string changes)
    {
        using var document = JsonDocument.Parse(changes);
        return document.RootElement.Clone();
    }
}
