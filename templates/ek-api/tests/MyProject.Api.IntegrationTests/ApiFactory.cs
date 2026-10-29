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
#if (UseSqlite)
using Microsoft.Data.Sqlite;
#endif
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
#if (UseOidc)
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
#endif
#if (UsePostgres)
using Testcontainers.PostgreSql;
#elif (UseSqlServer)
using Testcontainers.MsSql;
#endif

[assembly: AssemblyFixture(typeof(MyProject.Api.IntegrationTests.ApiFactory))]

namespace MyProject.Api.IntegrationTests;

/// <summary>
/// One running API for the whole test assembly, in the Testing environment.
#if (UsePostgres)
/// It owns a throwaway PostgreSQL container and applies the migrations once before the first test.
#elif (UseSqlServer)
/// It owns a throwaway SQL Server container and applies the migrations once before the first test.
#elif (UseSqlite)
/// It owns a throwaway SQLite file and applies the migrations once before the first test.
#endif
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
#if (UsePostgres)
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:18-alpine").Build();

#elif (UseSqlServer)
    private readonly MsSqlContainer _database = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU21-ubuntu-22.04").Build();

#elif (UseSqlite)
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"MyProject-tests-{Guid.NewGuid():N}.db");

#endif
#if (UseIdentity)
    public CapturedEmails Emails { get; } = new();

#endif
    public FakeLogCollector Logs => Services.GetFakeLogCollector();

#if (UsePostgres || UseSqlServer)
    public async ValueTask InitializeAsync()
    {
        await _database.StartAsync();

        // Starts the host, which applies the migrations.
        _ = Server;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _database.DisposeAsync();
    }
#elif (UseSqlite)
    public ValueTask InitializeAsync()
    {
        // Starts the host, which creates the file and applies the migrations.
        _ = Server;
        return ValueTask.CompletedTask;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.Delete(_databasePath + suffix);
        }
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
#if (UseDatabase)
#if (UseSqlite)
        builder.UseSetting("ConnectionStrings:Default", $"Data Source={_databasePath}");
#else
        builder.UseSetting("ConnectionStrings:Default", _database.GetConnectionString());
#endif
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
