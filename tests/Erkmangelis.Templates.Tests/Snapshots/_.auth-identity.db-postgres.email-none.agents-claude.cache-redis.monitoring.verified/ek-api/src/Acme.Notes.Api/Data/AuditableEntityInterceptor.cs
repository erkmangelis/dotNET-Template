using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Acme.Notes.Api.Data;

/// <summary>
/// Runs before every save, so no endpoint can forget it and no client can set what it sets:
/// turns deletes of <see cref="ISoftDeletable"/> entities into updates, fills in
/// <see cref="IRecordMetadata"/>, and writes an <see cref="AuditEntry"/> for each change to an
/// <see cref="IAudited"/> entity.
/// </summary>
internal sealed class AuditableEntityInterceptor(TimeProvider time, ICurrentActor actor) : SaveChangesInterceptor
{
    // Kept out of the recorded changes: the audit entry already says when and who.
    private static readonly HashSet<string> _unaudited =
    [
        nameof(IRecordMetadata.CreatedAt),
        nameof(IRecordMetadata.UpdatedAt),
        nameof(IRecordMetadata.CreatedBy),
        nameof(IRecordMetadata.UpdatedBy),
        nameof(ISoftDeletable.DeletedBy),
        nameof(ISoftDeletable.DeletedAt),
    ];

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Apply(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = RecordMetadata.Now(time);
        var actorId = actor.Id;
        var softDeleted = new HashSet<EntityEntry>();
        foreach (var entry in context.ChangeTracker.Entries().ToList())
        {
            if (entry is { State: EntityState.Deleted, Entity: ISoftDeletable deletable })
            {
                entry.State = EntityState.Modified;
                deletable.DeletedAt = now;
                deletable.DeletedBy = actorId;
                softDeleted.Add(entry);
            }

            if (entry is { State: EntityState.Added or EntityState.Modified, Entity: IRecordMetadata metadata })
            {
                RecordMetadata.Stamp(metadata, entry.State == EntityState.Added, now, actorId);
            }

            if (entry.Entity is IAudited && entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            {
                var action = softDeleted.Contains(entry) || entry.State == EntityState.Deleted
                    ? AuditAction.Deleted
                    : entry.State == EntityState.Added ? AuditAction.Created : AuditAction.Updated;
                var changes = Changes(entry, action);
                if (action == AuditAction.Updated && changes.Count == 0)
                {
                    continue;
                }

                context.Add(new AuditEntry
                {
                    Id = Guid.CreateVersion7(now),
                    EntityType = entry.Metadata.ClrType.Name,
                    EntityId = string.Join(',', entry.Metadata.FindPrimaryKey()!.Properties.Select(key => entry.Property(key.Name).CurrentValue)),
                    Action = action,
                    Changes = JsonSerializer.Serialize(changes, JsonSerializerOptions.Web),
                    ActorId = actorId,
                    OccurredAt = now,
                    TraceId = Activity.Current?.TraceId.ToString(),
                });
            }
        }
    }

    /// <summary>Each changed property's value before and after. A deletion records nothing: the row keeps its last values.</summary>
    private static Dictionary<string, AuditChange> Changes(EntityEntry entry, AuditAction action)
    {
        var changes = new Dictionary<string, AuditChange>(StringComparer.Ordinal);
        if (action == AuditAction.Deleted)
        {
            return changes;
        }

        foreach (var property in entry.Properties)
        {
            var metadata = property.Metadata;
            if (_unaudited.Contains(metadata.Name) || metadata.IsConcurrencyToken || metadata.IsShadowProperty())
            {
                continue;
            }

            if (action == AuditAction.Created)
            {
                changes[metadata.Name] = new AuditChange(null, property.CurrentValue);
            }
            else if (property.IsModified && !Equals(property.OriginalValue, property.CurrentValue))
            {
                changes[metadata.Name] = new AuditChange(property.OriginalValue, property.CurrentValue);
            }
        }

        return changes;
    }
}
