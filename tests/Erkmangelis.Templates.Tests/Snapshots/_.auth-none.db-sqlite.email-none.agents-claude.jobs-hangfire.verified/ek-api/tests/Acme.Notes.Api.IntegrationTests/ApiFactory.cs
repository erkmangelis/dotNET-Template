using Hangfire;
using Hangfire.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;

[assembly: AssemblyFixture(typeof(Acme.Notes.Api.IntegrationTests.ApiFactory))]

namespace Acme.Notes.Api.IntegrationTests;

/// <summary>
/// One running API for the whole test assembly, in the Testing environment.
/// It owns a throwaway SQLite file and applies the migrations once before the first test.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"Acme.Notes-tests-{Guid.NewGuid():N}.db");

    // Set once this app is up. Apps that tests start from it (WithWebHostBuilder) then run no Hangfire
    // server, so they never take this app's jobs and stop in the middle of one.
    private bool _started;

    public FakeLogCollector Logs => Services.GetFakeLogCollector();

    public ValueTask InitializeAsync()
    {
        // Starts the host, which creates the file and applies the migrations.
        _ = Server;
        _started = true;
        return ValueTask.CompletedTask;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        // The database, its journal files and the files jobs keep next to it (job storage, lock files).
        foreach (var file in Directory.EnumerateFiles(Path.GetTempPath(), $"{Path.GetFileNameWithoutExtension(_databasePath)}*"))
        {
            File.Delete(file);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("RateLimiting:Enabled", "false");
        builder.UseSetting("Api:UseHttpsRedirection", "false");
        builder.UseSetting("ConnectionStrings:Default", $"Data Source={_databasePath}");
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Jobs:MaxAttempts", "2");
        builder.UseSetting("Jobs:RetryDelaySeconds", "0");
        builder.UseSetting("Jobs:Dashboard", "true");
        builder.UseSetting("Jobs:PollIntervalSeconds", "1");
        builder.UseSetting("Jobs:RunServer", _started ? "false" : "true");
        builder.ConfigureLogging(logging => logging.AddFakeLogging());
        builder.ConfigureTestServices(services =>
        {
            services.AddScoped<FlakyJob>();
            WithoutHangfireLogs(services);
        });
    }

    /// <summary>
    /// Hangfire logs through a static that every app it starts replaces. An app a test starts would
    /// leave it on a disposed logger when it stops, so tests leave Hangfire's logs out.
    /// </summary>
    internal static void WithoutHangfireLogs(IServiceCollection services)
    {
        var hangfire = services.Single(service => service.ServiceType == typeof(IGlobalConfiguration));
        services.Replace(ServiceDescriptor.Singleton<IGlobalConfiguration>(provider =>
        {
            var configuration = (IGlobalConfiguration)hangfire.ImplementationFactory!(provider);
            configuration.UseLogProvider(new AspNetCoreLogProvider(NullLoggerFactory.Instance));
            return configuration;
        }));
    }
}
