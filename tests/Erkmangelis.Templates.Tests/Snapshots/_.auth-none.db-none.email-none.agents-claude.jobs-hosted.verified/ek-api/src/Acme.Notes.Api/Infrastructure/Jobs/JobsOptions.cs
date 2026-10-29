using System.ComponentModel.DataAnnotations;

namespace Acme.Notes.Api.Infrastructure.Jobs;

/// <summary>Background job settings, bound from the <c>Jobs</c> section.</summary>
internal sealed class JobsOptions
{
    public const string SectionName = "Jobs";

    /// <summary>How many queued jobs can wait. Queueing beyond it waits for room.</summary>
    [Range(1, 100_000)]
    public int QueueCapacity { get; set; } = 1_000;

    /// <summary>How many jobs run at the same time in this app.</summary>
    [Range(1, 100)]
    public int WorkerCount { get; set; } = 2;

    /// <summary>Cron schedules (UTC) that replace a recurring job's default, by job name.</summary>
    public Dictionary<string, string> Schedules { get; set; } = [];

    public string ScheduleFor(RecurringJobDefinition job) => Schedules.GetValueOrDefault(job.Name) ?? job.Cron;
}
