using Hangfire;
using Hangfire.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Acme.Notes.Api.Infrastructure.Jobs;

internal static class JobsSetup
{
    public const string DashboardPath = "/jobs";

    public static WebApplicationBuilder AddJobs(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddOptions<JobsOptions>()
            .BindConfiguration(JobsOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<JobIdempotency>();
        services.AddHangfire((provider, hangfire) =>
        {
            var connectionString = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString;
            var options = provider.GetRequiredService<IOptions<JobsOptions>>().Value;
            var pollInterval = TimeSpan.FromSeconds(options.PollIntervalSeconds);

            // Replaces Hangfire's default of ten retries over about a day with the Jobs settings.
            GlobalJobFilters.Filters.Remove<AutomaticRetryAttribute>();
            hangfire.UseFilter(new AutomaticRetryAttribute
            {
                Attempts = options.MaxAttempts - 1,
                DelaysInSeconds = [.. Enumerable.Range(2, Math.Max(options.MaxAttempts - 1, 1)).Select(attempt => (int)options.RetryDelayBefore(attempt).TotalSeconds)],
                OnAttemptsExceeded = AttemptsExceededAction.Fail,
            });
            hangfire
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                // Hangfire keeps its tables in the "hangfire" schema and creates them on first use.
                .UseSqlServerStorage(
                    () => new SqlConnection(connectionString),
                    new SqlServerStorageOptions { SchemaName = "hangfire", QueuePollInterval = pollInterval });
        });

        // Read before the app is built: in a split deployment only the worker runs the server.
        var jobs = builder.Configuration.GetSection(JobsOptions.SectionName).Get<JobsOptions>() ?? new JobsOptions();
        if (jobs.RunServer)
        {
            services.AddHangfireServer(server =>
            {
                server.Queues = [jobs.Queue];
                server.WorkerCount = jobs.WorkerCount;
                // Retries wait in the scheduled state; check for due ones as often as for new jobs.
                server.SchedulePollingInterval = TimeSpan.FromSeconds(jobs.PollIntervalSeconds);
            });
        }

        services.AddSingleton<IJobQueue, HangfireJobQueue>();
        services.AddSingleton<IFailedJobs, HangfireFailedJobs>();
        services.AddHostedService<HangfireRecurringJobs>();
        return builder;
    }

    /// <summary>Runs <typeparamref name="TJob"/> on <paramref name="cron"/> (UTC), unless <c>Jobs:Schedules:{name}</c> says otherwise.</summary>
    public static IServiceCollection AddRecurringJob<TJob>(this IServiceCollection services, string name, string cron)
        where TJob : class, IRecurringJob
    {
        services.AddScoped<TJob>();
        services.AddSingleton(new RecurringJobDefinition(name, typeof(TJob), cron));
        return services;
    }

    /// <summary>
    /// Serves the Hangfire dashboard at <see cref="DashboardPath"/> when <c>Jobs:Dashboard</c> is on, to
    /// users with <see cref="Permissions.JobsManage"/>. A browser reaches it with a cookie session.
    /// </summary>
    public static WebApplication MapJobsDashboard(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<IOptions<JobsOptions>>().Value.Dashboard)
        {
            return app;
        }

        // The endpoint's authorization replaces Hangfire's own filter, which only lets in local requests.
        app.MapHangfireDashboard(DashboardPath, new DashboardOptions { Authorization = [], DisplayStorageConnectionString = false })
            .RequirePermission(Permissions.JobsManage);
        return app;
    }
}
