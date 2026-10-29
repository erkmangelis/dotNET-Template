using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MyProject.Api.Data;

/// <summary>Fills in <see cref="IRecordMetadata"/> on every save, so no endpoint can forget it or let a client set it.</summary>
#if (UseAuth)
internal sealed class RecordMetadataInterceptor(TimeProvider time, ICurrentActor actor) : SaveChangesInterceptor
#else
internal sealed class RecordMetadataInterceptor(TimeProvider time) : SaveChangesInterceptor
#endif
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = RecordMetadata.Now(time);
        foreach (var entry in context.ChangeTracker.Entries<IRecordMetadata>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
#if (UseAuth)
                RecordMetadata.Stamp(entry.Entity, entry.State == EntityState.Added, now, actor.Id);
#else
                RecordMetadata.Stamp(entry.Entity, entry.State == EntityState.Added, now);
#endif
            }
        }
    }
}
