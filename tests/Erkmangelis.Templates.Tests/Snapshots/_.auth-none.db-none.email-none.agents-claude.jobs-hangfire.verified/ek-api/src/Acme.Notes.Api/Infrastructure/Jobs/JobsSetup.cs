using Hangfire;
using Hangfire.PostgreSql;
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

        services.AddHangfire((provider, hangfire) =>
        {
            var connectionString = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString;
            var pollInterval = TimeSpan.FromSeconds(provider.GetRequiredService<IOptions<JobsOptions>>().Value.PollIntervalSeconds);
            hangfire
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                // Hangfire keeps its tables in the "hangfire" schema and creates them on first use.
                .UsePostgreSqlStorage(
                    storage => storage.UseNpgsqlConnection(connectionString),
                    new PostgreSqlStorageOptions { SchemaName = "hangfire", QueuePollInterval = pollInterval });
        });

        // Read before the app is built: in a split deployment only the worker runs the server.
        var jobs = builder.Configuration.GetSection(JobsOptions.SectionName).Get<JobsOptions>() ?? new JobsOptions();
        if (jobs.RunServer)
        {
            services.AddHangfireServer(server =>
            {
                server.Queues = [jobs.Queue];
                server.WorkerCount = jobs.WorkerCount;
            });
        }

        services.AddSingleton<IJobQueue, HangfireJobQueue>();
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
    /// Serves the Hangfire dashboard at <see cref="DashboardPath"/> when <c>Jobs:Dashboard</c> is on. With
    /// no sign-in in this API, Hangfire lets in only requests from the same machine.
    /// </summary>
    public static WebApplication MapJobsDashboard(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<IOptions<JobsOptions>>().Value.Dashboard)
        {
            return app;
        }

        app.MapHangfireDashboard(DashboardPath, new DashboardOptions { DisplayStorageConnectionString = false });
        return app;
    }
}
