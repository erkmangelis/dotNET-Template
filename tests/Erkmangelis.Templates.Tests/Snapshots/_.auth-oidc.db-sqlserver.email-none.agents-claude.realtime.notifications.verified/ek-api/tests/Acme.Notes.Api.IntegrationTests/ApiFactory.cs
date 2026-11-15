using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Testcontainers.MsSql;

[assembly: AssemblyFixture(typeof(Acme.Notes.Api.IntegrationTests.ApiFactory))]

namespace Acme.Notes.Api.IntegrationTests;

/// <summary>
/// One running API for the whole test assembly, in the Testing environment.
/// It owns a throwaway SQL Server container and applies the migrations once before the first test.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly MsSqlContainer _database = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU21-ubuntu-22.04").Build();

    // Set once this app is up. Apps that tests start from it (WithWebHostBuilder) then run no
    // background workers of their own (see WithoutDeliveryLoops and Jobs:RunServer), so they never
    // claim this app's work and stop in the middle of it, leaving it leased until the claim expires.
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
        builder.UseSetting("Oidc:Authority", TestTokens.Issuer);
        builder.UseSetting("Oidc:Audience", TestTokens.Audience);
        builder.UseSetting("Notifications:DispatchIntervalSeconds", "1");
        builder.UseSetting("Notifications:Push:Transport", "Log");
        builder.UseSetting("Notifications:Sms:Transport", "Log");
        for (var index = 0; index < 4; index++)
        {
            builder.UseSetting($"Notifications:RetryDelaysSeconds:{index}", "1");
        }

        builder.ConfigureLogging(logging => logging.AddFakeLogging());
        builder.ConfigureTestServices(services =>
        {
            if (_started)
            {
                WithoutDeliveryLoops(services);
            }
            // Trust the test signing key instead of fetching the provider's metadata.
            services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.Configuration = new OpenIdConnectConfiguration { Issuer = TestTokens.Issuer };
                options.Configuration.SigningKeys.Add(TestTokens.SigningKey);
            });
        });
    }

    /// <summary>Removes the loops that claim outbox rows, for apps a test starts next to the shared one.</summary>
    private static void WithoutDeliveryLoops(IServiceCollection services)
    {
        Type[] loops =
        [
            typeof(NotificationDispatchLoop),
        ];
        foreach (var loop in services.Where(service => service.ServiceType == typeof(IHostedService) && loops.Contains(service.ImplementationType)).ToList())
        {
            services.Remove(loop);
        }
    }
}
