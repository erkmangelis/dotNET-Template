using System.ComponentModel.DataAnnotations;

namespace Acme.Notes.Api.Infrastructure.Jobs;

/// <summary>Background job settings, bound from the <c>Jobs</c> section.</summary>
internal sealed class JobsOptions
{
    public const string SectionName = "Jobs";

    /// <summary>The Hangfire queue this app puts jobs on and, when it runs the server, takes them from.</summary>
    [Required]
    [RegularExpression("^[a-z0-9_-]+$", ErrorMessage = "Jobs:Queue may hold only lower-case letters, digits, '_' and '-'.")]
    public string Queue { get; set; } = "default";

    /// <summary>Whether this app runs jobs. Turn it off where another process, such as a worker, runs them.</summary>
    public bool RunServer { get; set; } = true;

    /// <summary>Serves the Hangfire dashboard at <c>/jobs</c>.</summary>
    public bool Dashboard { get; set; }

    /// <summary>How often an idle server asks the database for new jobs.</summary>
    [Range(1, 300)]
    public int PollIntervalSeconds { get; set; } = 5;

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
