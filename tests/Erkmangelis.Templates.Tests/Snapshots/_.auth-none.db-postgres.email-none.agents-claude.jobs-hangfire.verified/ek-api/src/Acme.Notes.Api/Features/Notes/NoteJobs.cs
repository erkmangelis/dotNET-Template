using System.ComponentModel.DataAnnotations;

namespace Acme.Notes.Api.Features.Notes;

/// <summary>Notes to create in the background. Each one is checked like a single create.</summary>
public sealed record ImportNotesRequest
{
    public const int MaxNotes = 100;

    [Required]
    [MinLength(1)]
    [MaxLength(MaxNotes)]
    public IReadOnlyList<CreateNoteRequest> Notes { get; init; } = [];
}

/// <param name="ImportId">Names this import in the logs.</param>
/// <param name="JobId">The background job that creates the notes.</param>
public sealed record ImportNotesResponse(Guid ImportId, string JobId);

/// <summary>
/// What <see cref="ImportNotesJob"/> needs, as plain values: a durable queue stores it as JSON. Use
/// arrays rather than interfaces such as IReadOnlyList, so the stored JSON names no concrete list type.
/// </summary>
internal sealed record NoteImport(Guid ImportId, CreateNoteRequest[] Notes);

/// <summary>
/// Creates the notes of an import, once: a second run of the same import (a retry or a second
/// delivery) changes nothing, even when a note it created was deleted since. A title the owner
/// already uses is skipped, as a single create would refuse it.
/// </summary>
internal sealed partial class ImportNotesJob(
    INoteStore store,
    JobIdempotency idempotency,
    TimeProvider time,
    ILogger<ImportNotesJob> logger) : IJob<NoteImport>
{
    public async Task RunAsync(NoteImport import, CancellationToken cancellationToken)
    {
        if (!await idempotency.RunOnceAsync(nameof(ImportNotesJob), import.ImportId.ToString(), token => ImportAsync(import, token), cancellationToken))
        {
            LogAlreadyDone(logger, import.ImportId);
        }
    }

    private async Task ImportAsync(NoteImport import, CancellationToken cancellationToken)
    {
        var created = 0;
        foreach (var item in import.Notes)
        {
            var note = new Note
            {
                Id = Guid.CreateVersion7(time.GetUtcNow()),
                Title = item.Title.Trim(),
                Content = item.Content,
            };
            if (await store.TryAddAsync(note, cancellationToken))
            {
                created++;
            }
        }

        LogImported(logger, import.ImportId, created, import.Notes.Length - created);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Note import {ImportId} created {Created} notes and skipped {Skipped} duplicate titles")]
    private static partial void LogImported(ILogger logger, Guid importId, int created, int skipped);

    [LoggerMessage(Level = LogLevel.Information, Message = "Note import {ImportId} already ran; nothing to do")]
    private static partial void LogAlreadyDone(ILogger logger, Guid importId);
}

/// <summary>Removes notes for good <see cref="RetentionPeriod"/> after they were deleted. Their audit history stays.</summary>
internal sealed partial class PurgeDeletedNotesJob(INoteStore store, TimeProvider time, ILogger<PurgeDeletedNotesJob> logger) : IRecurringJob
{
    public const string Name = "purge-deleted-notes";

    /// <summary>Every day at 03:00 UTC.</summary>
    public const string Cron = "0 3 * * *";

    public static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(30);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var purged = await store.PurgeDeletedAsync(time.GetUtcNow() - RetentionPeriod, cancellationToken);
        LogPurged(logger, purged);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Purged {Count} notes deleted more than 30 days ago")]
    private static partial void LogPurged(ILogger logger, int count);
}
