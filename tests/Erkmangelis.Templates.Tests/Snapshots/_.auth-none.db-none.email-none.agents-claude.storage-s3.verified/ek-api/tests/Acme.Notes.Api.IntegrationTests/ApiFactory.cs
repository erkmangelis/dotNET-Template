using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    internal const string StorageAccessKey = "test-access-key";
    internal const string StorageSecretKey = "test-secret-key";

    // RustFS, the same S3-compatible storage compose runs.
    private readonly IContainer _storage = new ContainerBuilder("rustfs/rustfs:1.0.4")
        .WithEnvironment("RUSTFS_ACCESS_KEY", StorageAccessKey)
        .WithEnvironment("RUSTFS_SECRET_KEY", StorageSecretKey)
        .WithPortBinding(9000, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(9000).ForPath("/health")))
        .Build();

    private readonly string _uploadDirectory = Path.Combine(Path.GetTempPath(), $"Acme.Notes-tests-uploads-{Guid.NewGuid():N}");

    /// <summary>The storage's address from the tests, which is also the address signed links use.</summary>
    internal string StorageUrl => $"http://{_storage.Hostname}:{_storage.GetMappedPublicPort(9000)}";

    public FakeLogCollector Logs => Services.GetFakeLogCollector();

    public async ValueTask InitializeAsync()
    {
        await _database.StartAsync();
        await _storage.StartAsync();

        // Starts the host, which applies the migrations.
        _ = Server;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _database.DisposeAsync();
        await _storage.DisposeAsync();
        if (Directory.Exists(_uploadDirectory))
        {
            Directory.Delete(_uploadDirectory, recursive: true);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("RateLimiting:Enabled", "false");
        builder.UseSetting("Api:UseHttpsRedirection", "false");
        builder.UseSetting("ConnectionStrings:Default", _database.GetConnectionString());
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Storage:ServiceUrl", StorageUrl);
        builder.UseSetting("Storage:AccessKey", StorageAccessKey);
        builder.UseSetting("Storage:SecretKey", StorageSecretKey);
        builder.UseSetting("Storage:CreateBucket", "true");
        builder.UseSetting("Files:Tus:Directory", _uploadDirectory);
        builder.ConfigureLogging(logging => logging.AddFakeLogging());
    }
}
