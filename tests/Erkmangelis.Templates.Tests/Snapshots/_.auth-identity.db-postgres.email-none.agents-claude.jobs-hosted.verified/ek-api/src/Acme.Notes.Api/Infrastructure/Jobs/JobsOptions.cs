using System.ComponentModel.DataAnnotations;

namespace Acme.Notes.Api.Infrastructure.Jobs;

/// <summary>Background job settings, bound from the <c>Jobs</c> section.</summary>
internal sealed class JobsOptions
{
    public const string SectionName = "Jobs";

    /// <summary>How many queued jobs can wait. Queueing beyond it waits for room.</summary>
    [Range(1, 100_000)]
    public int QueueCapacity { get; set; } = 1_000;

    /// <summary>How many times a job runs before it counts as failed: the first run and the retries.</summary>
    [Range(1, 20)]
    public int MaxAttempts { get; set; } = 5;

    /// <summary>The wait before the first retry. Each later retry waits twice as long, up to an hour.</summary>
    [Range(0, 3600)]
    public int RetryDelaySeconds { get; set; } = 10;

    /// <summary>How long <see cref="JobIdempotency"/> remembers that a job's key is done.</summary>
    [Range(1, 365)]
    public int IdempotencyRetentionDays { get; set; } = 7;

    /// <summary>How many jobs run at the same time in this app.</summary>
    [Range(1, 100)]
    public int WorkerCount { get; set; } = 2;

    /// <summary>Cron schedules (UTC) that replace a recurring job's default, by job name.</summary>
    public Dictionary<string, string> Schedules { get; set; } = [];

    public string ScheduleFor(RecurringJobDefinition job) => Schedules.GetValueOrDefault(job.Name) ?? job.Cron;

    /// <summary>The wait before attempt <paramref name="attempt"/> (2 is the first retry).</summary>
    public TimeSpan RetryDelayBefore(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(RetryDelaySeconds * Math.Pow(2, attempt - 2), TimeSpan.FromHours(1).TotalSeconds));
}
