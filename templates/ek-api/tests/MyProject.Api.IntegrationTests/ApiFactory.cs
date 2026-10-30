#if (UseHangfire)
using Hangfire;
using Hangfire.AspNetCore;
#endif
#if (UseOidc)
using Microsoft.AspNetCore.Authentication.JwtBearer;
#endif
using Microsoft.AspNetCore.Hosting;
#if (UseIdentity && !UseSmtp)
using Microsoft.AspNetCore.Identity;
#endif
using Microsoft.AspNetCore.Mvc.Testing;
#if (UseAuth || UseJobs)
using Microsoft.AspNetCore.TestHost;
#endif
#if (UseSqlite)
using Microsoft.Data.Sqlite;
#endif
using Microsoft.Extensions.DependencyInjection;
#if (UseHangfire)
using Microsoft.Extensions.DependencyInjection.Extensions;
#endif
using Microsoft.Extensions.Logging;
#if (UseHangfire)
using Microsoft.Extensions.Logging.Abstractions;
#endif
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
#if (UseWorker)
#if (UseSqlite)
    internal string ConnectionString => $"Data Source={_databasePath}";
#else
    internal string ConnectionString => _database.GetConnectionString();
#endif

#endif
#if (UseIdentity)
    public CapturedEmails Emails { get; } = new();

#endif
#if (UseHangfire)
    // Set once this app is up. Apps that tests start from it (WithWebHostBuilder) then run no Hangfire
    // server, so they never take this app's jobs and stop in the middle of one.
    private bool _started;

#endif
    public FakeLogCollector Logs => Services.GetFakeLogCollector();

#if (UsePostgres || UseSqlServer)
    public async ValueTask InitializeAsync()
    {
        await _database.StartAsync();

        // Starts the host, which applies the migrations.
        _ = Server;
#if (UseHangfire)
        _started = true;
#endif
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
#if (UseHangfire)
        _started = true;
#endif
        return ValueTask.CompletedTask;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
#if (UseJobs)
        // The database, its journal files and the files jobs keep next to it (job storage, lock files).
        foreach (var file in Directory.EnumerateFiles(Path.GetTempPath(), $"{Path.GetFileNameWithoutExtension(_databasePath)}*"))
        {
            File.Delete(file);
        }
#else
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            File.Delete(_databasePath + suffix);
        }
#endif
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
#if (UseJobs)
        builder.UseSetting("Jobs:MaxAttempts", "2");
        builder.UseSetting("Jobs:RetryDelaySeconds", "0");
#endif
#if (UseHangfire)
        builder.UseSetting("Jobs:Dashboard", "true");
        builder.UseSetting("Jobs:PollIntervalSeconds", "1");
        builder.UseSetting("Jobs:RunServer", _started ? "false" : "true");
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
#if (UseAuth || UseJobs)
        builder.ConfigureTestServices(services =>
        {
#if (UseJobs)
            services.AddScoped<FlakyJob>();
#endif
#if (UseHostedJobs)
            services.AddScoped<CountingRecurringJob>();
#endif
#if (UseHangfire)
            WithoutHangfireLogs(services);
#endif
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
#if (UseHangfire)

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
#endif
}
