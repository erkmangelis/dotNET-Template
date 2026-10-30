namespace Acme.Notes.Api.Common;

/// <summary>When an entity was created and last changed. Filled in when it is saved, never from a request.</summary>
internal interface IRecordMetadata
{
    DateTimeOffset CreatedAt { get; set; }

    DateTimeOffset UpdatedAt { get; set; }
}

internal static class RecordMetadata
{
    /// <summary>The current time in whole microseconds, the precision databases usually store, so responses match later reads.</summary>
    public static DateTimeOffset Now(TimeProvider time)
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
    }

    public static void Stamp(IRecordMetadata entity, bool added, DateTimeOffset now)
    {
        if (added)
        {
            // Seed data and imports may bring their own creation time.
            if (entity.CreatedAt == default)
            {
                entity.CreatedAt = now;
            }

            entity.UpdatedAt = entity.CreatedAt;
        }
        else
        {
            entity.UpdatedAt = now;
        }
    }
}
