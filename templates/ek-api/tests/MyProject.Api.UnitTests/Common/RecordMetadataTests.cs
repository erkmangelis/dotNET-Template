namespace MyProject.Api.UnitTests.Common;

public sealed class RecordMetadataTests
{
    private static readonly DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_record_is_created_and_updated_now()
    {
        var record = new Record();

#if (UseAuth)
        RecordMetadata.Stamp(record, added: true, _now, "user-1");

        Assert.Equal((_now, _now, "user-1", "user-1"), (record.CreatedAt, record.UpdatedAt, record.CreatedBy, record.UpdatedBy));
#else
        RecordMetadata.Stamp(record, added: true, _now);

        Assert.Equal((_now, _now), (record.CreatedAt, record.UpdatedAt));
#endif
    }

    [Fact]
    public void A_new_record_keeps_a_creation_time_it_brings()
    {
        var record = new Record { CreatedAt = _now.AddDays(-1) };

#if (UseAuth)
        RecordMetadata.Stamp(record, added: true, _now, actorId: null);
#else
        RecordMetadata.Stamp(record, added: true, _now);
#endif

        Assert.Equal(_now.AddDays(-1), record.UpdatedAt);
    }

    [Fact]
    public void A_change_moves_only_the_update()
    {
        var record = new Record();
#if (UseAuth)
        RecordMetadata.Stamp(record, added: true, _now, "user-1");

        RecordMetadata.Stamp(record, added: false, _now.AddHours(1), "user-2");

        Assert.Equal((_now, _now.AddHours(1), "user-1", "user-2"), (record.CreatedAt, record.UpdatedAt, record.CreatedBy, record.UpdatedBy));
#else
        RecordMetadata.Stamp(record, added: true, _now);

        RecordMetadata.Stamp(record, added: false, _now.AddHours(1));

        Assert.Equal((_now, _now.AddHours(1)), (record.CreatedAt, record.UpdatedAt));
#endif
    }

    [Fact]
    public void Now_drops_sub_microsecond_ticks()
    {
        var time = new FixedTime(_now.AddTicks(7));

        Assert.Equal(_now, RecordMetadata.Now(time));
    }

    private sealed class Record : IRecordMetadata
    {
        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }
#if (UseAuth)

        public string? CreatedBy { get; set; }

        public string? UpdatedBy { get; set; }
#endif
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
