namespace MyProject.Api.Common;

#if (UseAuth)
/// <summary>
/// When an entity was created and last changed, and by whom. Filled in when it is saved, never from a
/// request; the actor is the signed-in user's id, or null for changes outside a request.
/// </summary>
#else
/// <summary>When an entity was created and last changed. Filled in when it is saved, never from a request.</summary>
#endif
internal interface IRecordMetadata
{
    DateTimeOffset CreatedAt { get; set; }

    DateTimeOffset UpdatedAt { get; set; }
#if (UseAuth)

    string? CreatedBy { get; set; }

    string? UpdatedBy { get; set; }
#endif
}

internal static class RecordMetadata
{
    /// <summary>The current time in whole microseconds, the precision databases usually store, so responses match later reads.</summary>
    public static DateTimeOffset Now(TimeProvider time)
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
    }

#if (UseAuth)
    public static void Stamp(IRecordMetadata entity, bool added, DateTimeOffset now, string? actorId)
#else
    public static void Stamp(IRecordMetadata entity, bool added, DateTimeOffset now)
#endif
    {
        if (added)
        {
            // Seed data and imports may bring their own creation time.
            if (entity.CreatedAt == default)
            {
                entity.CreatedAt = now;
            }

            entity.UpdatedAt = entity.CreatedAt;
#if (UseAuth)
            entity.CreatedBy ??= actorId;
            entity.UpdatedBy = entity.CreatedBy;
#endif
        }
        else
        {
            entity.UpdatedAt = now;
#if (UseAuth)
            entity.UpdatedBy = actorId;
#endif
        }
    }
}
