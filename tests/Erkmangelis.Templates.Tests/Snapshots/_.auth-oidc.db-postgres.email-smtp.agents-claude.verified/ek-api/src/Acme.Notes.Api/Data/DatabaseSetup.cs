using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Acme.Notes.Api.Data;

internal static class DatabaseSetup
{
    public static WebApplicationBuilder AddDatabase(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<DatabaseOptions>()
            .BindConfiguration(DatabaseOptions.SectionName)
            .Configure<IConfiguration>((options, configuration) =>
                options.ConnectionString ??= configuration.GetConnectionString("Default"))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.AddSingleton<RecordMetadataInterceptor>();
        builder.Services.AddDbContext<AppDbContext>((services, options) => options
            .UseNpgsql(services.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString)
            .AddInterceptors(services.GetRequiredService<RecordMetadataInterceptor>()));

        builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>(tags: [Observability.ReadyTag]);
        builder.Services.AddScoped<DevelopmentSeeder>();

        return builder;
    }

    /// <summary>Applies migrations and development seed data when the settings ask for it.</summary>
    public static async Task PrepareDatabaseAsync(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        if (!options.MigrateOnStartup && !options.SeedDevelopmentData)
        {
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        if (options.MigrateOnStartup)
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        }

        if (options.SeedDevelopmentData && app.Environment.IsDevelopment())
        {
            await scope.ServiceProvider.GetRequiredService<DevelopmentSeeder>().SeedAsync(CancellationToken.None);
        }
    }
}
