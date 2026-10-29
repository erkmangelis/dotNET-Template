using Hangfire;
using Hangfire.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Acme.Notes.Api.IntegrationTests.ApiFactory))]

namespace Acme.Notes.Api.IntegrationTests;

/// <summary>
/// One running API for the whole test assembly, in the Testing environment.
/// It owns a throwaway PostgreSQL container and applies the migrations once before the first test.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public CapturedEmails Emails { get; } = new();

    // Set once this app is up. Apps that tests start from it (WithWebHostBuilder) then run no Hangfire
    // server, so they never take this app's jobs and stop in the middle of one.
    private bool _started;

    public FakeLogCollector Logs => Services.GetFakeLogCollector();

    public async ValueTask InitializeAsync()
    {
        await _database.StartAsync();

        // Starts the host, which applies the migrations.
        _ = Server;
        _started = true;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _database.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("RateLimiting:Enabled", "false");
        builder.UseSetting("Api:UseHttpsRedirection", "false");
        builder.UseSetting("ConnectionStrings:Default", _database.GetConnectionString());
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Jobs:Dashboard", "true");
        builder.UseSetting("Jobs:PollIntervalSeconds", "1");
        if (_started)
        {
            builder.UseSetting("Jobs:RunServer", "false");
        }
        builder.ConfigureLogging(logging => logging.AddFakeLogging());
        builder.ConfigureTestServices(services =>
        {
            // Hangfire logs through a static that every app it starts replaces. An app a test starts
            // would leave it on a disposed logger when it stops, so tests leave Hangfire's logs out.
            var hangfire = services.Single(service => service.ServiceType == typeof(IGlobalConfiguration));
            services.Replace(ServiceDescriptor.Singleton<IGlobalConfiguration>(provider =>
            {
                var configuration = (IGlobalConfiguration)hangfire.ImplementationFactory!(provider);
                configuration.UseLogProvider(new AspNetCoreLogProvider(NullLoggerFactory.Instance));
                return configuration;
            }));
            services.AddSingleton<IEmailSender<AppUser>>(Emails);
        });
    }
}
