
namespace Acme.Notes.Api.Infrastructure.Jobs;

internal static class JobsSetup
{
    public static WebApplicationBuilder AddJobs(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddOptions<JobsOptions>()
            .BindConfiguration(JobsOptions.SectionName)
            .ValidateDataAnnotations()
            .Validate(
                options => options.Schedules.Values.All(RecurringJobScheduler.IsValidCron),
                "Jobs:Schedules values must be cron expressions with five fields.")
            .ValidateOnStart();

        services.AddScoped<JobIdempotency>();
        services.AddSingleton<IDistributedLock, PostgresDistributedLock>();
        services.AddSingleton<InProcessJobQueue>();
        services.AddSingleton<IJobQueue>(provider => provider.GetRequiredService<InProcessJobQueue>());
        services.AddSingleton<InProcessFailedJobs>();
        services.AddSingleton<IFailedJobs>(provider => provider.GetRequiredService<InProcessFailedJobs>());
        services.AddHostedService<InProcessJobRunner>();
        services.AddHostedService<RecurringJobScheduler>();
        services.AddScoped<RecurringJobRunner>();
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
}
