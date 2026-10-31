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

        builder.Services.AddSingleton<AuditableEntityInterceptor>();
        builder.Services.AddDbContext<AppDbContext>((services, options) => options
            .UseAppDatabase(services.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString)
            .AddInterceptors(services.GetRequiredService<AuditableEntityInterceptor>()));

        builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>(tags: [Observability.ReadyTag]);
        builder.Services.AddScoped<AuditLog>();
        builder.Services.AddScoped<DevelopmentSeeder>();

        return builder;
    }

    /// <summary>The database provider, in one place for the app and <see cref="AppDbContextFactory"/>.</summary>
    /// <param name="options">The context's options.</param>
    /// <param name="connectionString">The app's database.</param>
    /// <param name="migrationsHistoryTable">A feature context's own history table; null for the app's.</param>
    public static TBuilder UseAppDatabase<TBuilder>(this TBuilder options, string? connectionString, string? migrationsHistoryTable = null)
        where TBuilder : DbContextOptionsBuilder
    {
        options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable(migrationsHistoryTable ?? "__EFMigrationsHistory"));
        return options;
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
            foreach (var module in scope.ServiceProvider.GetServices<ModuleDatabase>())
            {
                await ((DbContext)scope.ServiceProvider.GetRequiredService(module.ContextType)).Database.MigrateAsync();
            }
        }

        if (options.SeedDevelopmentData && app.Environment.IsDevelopment())
        {
            await scope.ServiceProvider.GetRequiredService<DevelopmentSeeder>().SeedAsync(CancellationToken.None);
        }
    }
}
