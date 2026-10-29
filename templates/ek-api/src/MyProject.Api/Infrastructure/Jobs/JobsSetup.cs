#if (UseHangfire)
using Hangfire;
#if (UsePostgres)
using Hangfire.PostgreSql;
#elif (UseSqlServer)
using Hangfire.SqlServer;
#else
using Hangfire.Storage.SQLite;
#endif
#if (UseSqlServer)
using Microsoft.Data.SqlClient;
#elif (UseSqlite)
using Microsoft.Data.Sqlite;
#endif
using Microsoft.Extensions.Options;
#endif

namespace MyProject.Api.Infrastructure.Jobs;

internal static class JobsSetup
{
#if (UseHangfire)
    public const string DashboardPath = "/jobs";

#endif
    public static WebApplicationBuilder AddJobs(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddOptions<JobsOptions>()
            .BindConfiguration(JobsOptions.SectionName)
            .ValidateDataAnnotations()
#if (UseHostedJobs)
            .Validate(
                options => options.Schedules.Values.All(RecurringJobScheduler.IsValidCron),
                "Jobs:Schedules values must be cron expressions with five fields.")
#endif
            .ValidateOnStart();

        services.AddScoped<JobIdempotency>();
#if (UsePostgres)
        services.AddSingleton<IDistributedLock, PostgresDistributedLock>();
#elif (UseSqlServer)
        services.AddSingleton<IDistributedLock, SqlServerDistributedLock>();
#elif (UseSqlite)
        services.AddSingleton<IDistributedLock, FileDistributedLock>();
#else
        services.AddSingleton<IDistributedLock, InProcessDistributedLock>();
#endif
#if (UseHostedJobs)
        services.AddSingleton<InProcessJobQueue>();
        services.AddSingleton<IJobQueue>(provider => provider.GetRequiredService<InProcessJobQueue>());
        services.AddSingleton<InProcessFailedJobs>();
        services.AddSingleton<IFailedJobs>(provider => provider.GetRequiredService<InProcessFailedJobs>());
        services.AddHostedService<InProcessJobRunner>();
        services.AddHostedService<RecurringJobScheduler>();
        services.AddScoped<RecurringJobRunner>();
#else
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
#if (UsePostgres)
                // Hangfire keeps its tables in the "hangfire" schema and creates them on first use.
                .UsePostgreSqlStorage(
                    storage => storage.UseNpgsqlConnection(connectionString),
                    new PostgreSqlStorageOptions { SchemaName = "hangfire", QueuePollInterval = pollInterval });
#elif (UseSqlServer)
                // Hangfire keeps its tables in the "hangfire" schema and creates them on first use.
                .UseSqlServerStorage(
                    () => new SqlConnection(connectionString),
                    new SqlServerStorageOptions { SchemaName = "hangfire", QueuePollInterval = pollInterval });
#else
                // A file of its own next to the app's database, so jobs never wait on the app's writes.
                .UseSQLiteStorage(SqliteJobsPath(connectionString), new SQLiteStorageOptions { QueuePollInterval = pollInterval });
#endif
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
#endif
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
#if (UseHangfire)

    /// <summary>
#if (UseAuth)
    /// Serves the Hangfire dashboard at <see cref="DashboardPath"/> when <c>Jobs:Dashboard</c> is on, to
    /// users with <see cref="Permissions.JobsManage"/>. A browser reaches it with a cookie session.
#else
    /// Serves the Hangfire dashboard at <see cref="DashboardPath"/> when <c>Jobs:Dashboard</c> is on. With
    /// no sign-in in this API, Hangfire lets in only requests from the same machine.
#endif
    /// </summary>
    public static WebApplication MapJobsDashboard(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<IOptions<JobsOptions>>().Value.Dashboard)
        {
            return app;
        }

#if (UseAuth)
        // The endpoint's authorization replaces Hangfire's own filter, which only lets in local requests.
        app.MapHangfireDashboard(DashboardPath, new DashboardOptions { Authorization = [], DisplayStorageConnectionString = false })
            .RequirePermission(Permissions.JobsManage);
#else
        app.MapHangfireDashboard(DashboardPath, new DashboardOptions { DisplayStorageConnectionString = false });
#endif
        return app;
    }
#if (UseSqlite)

    /// <summary>The jobs file for an app database such as <c>Data Source=/data/app.db</c>: <c>/data/app-jobs.db</c>.</summary>
    public static string SqliteJobsPath(string? connectionString)
    {
        var database = new SqliteConnectionStringBuilder(connectionString).DataSource;
        return Path.Combine(Path.GetDirectoryName(database) ?? string.Empty, $"{Path.GetFileNameWithoutExtension(database)}-jobs.db");
    }
#endif
#endif
}
