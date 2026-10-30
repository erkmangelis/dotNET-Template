using Hangfire;
using Hangfire.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
        builder.UseSetting("Jobs:MaxAttempts", "2");
        builder.UseSetting("Jobs:RetryDelaySeconds", "0");
        builder.UseSetting("Jobs:Dashboard", "true");
        builder.UseSetting("Jobs:PollIntervalSeconds", "1");
        builder.UseSetting("Jobs:RunServer", _started ? "false" : "true");
        builder.UseSetting("Oidc:Authority", TestTokens.Issuer);
        builder.UseSetting("Oidc:Audience", TestTokens.Audience);
        builder.ConfigureLogging(logging => logging.AddFakeLogging());
        builder.ConfigureTestServices(services =>
        {
            services.AddScoped<FlakyJob>();
            WithoutHangfireLogs(services);
            // Trust the test signing key instead of fetching the provider's metadata.
            services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.Configuration = new OpenIdConnectConfiguration { Issuer = TestTokens.Issuer };
                options.Configuration.SigningKeys.Add(TestTokens.SigningKey);
            });
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
