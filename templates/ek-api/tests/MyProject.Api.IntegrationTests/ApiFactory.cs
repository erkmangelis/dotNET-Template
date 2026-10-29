using Microsoft.AspNetCore.Hosting;
#if (UseIdentity)
using Microsoft.AspNetCore.Identity;
#endif
using Microsoft.AspNetCore.Mvc.Testing;
#if (UseIdentity)
using Microsoft.AspNetCore.TestHost;
#endif
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
#if (UsePostgres)
using Testcontainers.PostgreSql;
#endif

[assembly: AssemblyFixture(typeof(MyProject.Api.IntegrationTests.ApiFactory))]

namespace MyProject.Api.IntegrationTests;

/// <summary>
/// One running API for the whole test assembly, in the Testing environment.
#if (UsePostgres)
/// It owns a throwaway PostgreSQL container and applies the migrations once before the first test.
#endif
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
#if (UsePostgres)
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();

#endif
#if (UseIdentity)
    public CapturingEmailSender Emails { get; } = new();

#endif
    public FakeLogCollector Logs => Services.GetFakeLogCollector();

#if (UsePostgres)
    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        // Starts the host, which applies the migrations.
        _ = Server;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }
#else
    public ValueTask InitializeAsync()
    {
        _ = Server;
        return ValueTask.CompletedTask;
    }
#endif

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("RateLimiting:Enabled", "false");
        builder.UseSetting("Api:UseHttpsRedirection", "false");
#if (UsePostgres)
        builder.UseSetting("ConnectionStrings:Default", _postgres.GetConnectionString());
        builder.UseSetting("Database:MigrateOnStartup", "true");
#endif
        builder.ConfigureLogging(logging => logging.AddFakeLogging());
#if (UseIdentity)
        builder.ConfigureTestServices(services => services.AddSingleton<IEmailSender<AppUser>>(Emails));
#endif
    }
}
