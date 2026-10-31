using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
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

    public CapturedEmails Emails { get; } = new();

    /// <summary>Plays every webhook subscriber: outgoing deliveries land here instead of on the network.</summary>
    public WebhookReceiver Subscribers { get; } = new();

    /// <summary>What the test handler of incoming webhooks was given.</summary>
    public HandledWebhooks HandledWebhooks { get; } = new();

    public FakeLogCollector Logs => Services.GetFakeLogCollector();

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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("RateLimiting:Enabled", "false");
        builder.UseSetting("Api:UseHttpsRedirection", "false");
        builder.UseSetting("ConnectionStrings:Default", _database.GetConnectionString());
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting($"Webhooks:Incoming:Sources:{IncomingWebhookTests.Source}:Secrets:0", IncomingWebhookTests.Secret);
        builder.UseSetting($"Webhooks:Incoming:Sources:{IncomingWebhookTests.UnhandledSource}:Secrets:0", IncomingWebhookTests.Secret);
        builder.UseSetting("Webhooks:Incoming:ProcessIntervalSeconds", "1");
        builder.UseSetting("Webhooks:Outgoing:DispatchIntervalSeconds", "1");
        // Every retry after a second, so tests that fail on purpose finish quickly.
        for (var index = 0; index < 6; index++)
        {
            builder.UseSetting($"Webhooks:Incoming:RetryDelaysSeconds:{index}", "1");
            builder.UseSetting($"Webhooks:Outgoing:RetryDelaysSeconds:{index}", "1");
        }

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
            services.AddSingleton<IEmailSender<AppUser>>(Emails);
            services.AddHttpClient(WebhookDispatcher.ClientName).ConfigurePrimaryHttpMessageHandler(() => new WebhookReceiver.Handler(Subscribers));
            services.AddSingleton(HandledWebhooks);
            services.AddIncomingWebhookHandler<RecordingWebhookHandler>(IncomingWebhookTests.Source);
        });
    }
}
