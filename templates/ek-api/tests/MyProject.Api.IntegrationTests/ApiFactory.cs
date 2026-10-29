#if (UseOidc)
using Microsoft.AspNetCore.Authentication.JwtBearer;
#endif
using Microsoft.AspNetCore.Hosting;
#if (UseIdentity && !UseSmtp)
using Microsoft.AspNetCore.Identity;
#endif
using Microsoft.AspNetCore.Mvc.Testing;
#if (UseAuth)
using Microsoft.AspNetCore.TestHost;
#endif
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
#if (UseOidc)
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
#endif
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
    public CapturedEmails Emails { get; } = new();

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
#if (UseExternal)
        builder.UseSetting("ExternalAuth:Providers:Google:ClientIds:0", TestTokens.GoogleClientId);
#endif
#if (UseOidc)
        builder.UseSetting("Oidc:Authority", TestTokens.Issuer);
        builder.UseSetting("Oidc:Audience", TestTokens.Audience);
#endif
#if (UseSmtp)
        builder.UseSetting("Email:Transport", "Log");
        builder.UseSetting("Email:FromAddress", "noreply@example.com");
#endif
        builder.ConfigureLogging(logging => logging.AddFakeLogging());
#if (UseAuth)
        builder.ConfigureTestServices(services =>
        {
#if (UseIdentity && UseSmtp)
            services.AddSingleton<IEmailTransport>(Emails);
#elif (UseIdentity)
            services.AddSingleton<IEmailSender<AppUser>>(Emails);
#endif
#if (UseExternal)
            services.AddSingleton<IExternalProviderMetadata, TestProviderMetadata>();
#endif
#if (UseOidc)
            // Trust the test signing key instead of fetching the provider's metadata.
            services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.Configuration = new OpenIdConnectConfiguration { Issuer = TestTokens.Issuer };
                options.Configuration.SigningKeys.Add(TestTokens.SigningKey);
            });
#endif
        });
#endif
    }
}
