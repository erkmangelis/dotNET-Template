using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Acme.Notes.Api.IntegrationTests;

/// <summary>Sends real mail over SMTP to a throwaway Mailpit server and reads it back through Mailpit's API.</summary>
public sealed class SmtpEmailTransportTests : IAsyncLifetime
{
    private const int SmtpPort = 1025;
    private const int ApiPort = 8025;

    private readonly IContainer _mailpit = new ContainerBuilder("axllent/mailpit:v1.32")
        .WithPortBinding(SmtpPort, assignRandomHostPort: true)
        .WithPortBinding(ApiPort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(ApiPort).ForPath("/livez")))
        .Build();

    public async ValueTask InitializeAsync() => await _mailpit.StartAsync();

    public async ValueTask DisposeAsync() => await _mailpit.DisposeAsync();

    [Fact]
    public async Task A_message_arrives_with_its_subject_and_both_bodies()
    {
        var options = Options.Create(new EmailOptions
        {
            FromAddress = "noreply@example.com",
            FromName = "Acme",
            Smtp = new SmtpSettings
            {
                Host = _mailpit.Hostname,
                Port = _mailpit.GetMappedPublicPort(SmtpPort),
                Security = MailKit.Security.SecureSocketOptions.None,
            },
        });
        var message = new EmailTemplates(options).ConfirmEmail("user@example.com", "https://api.example.com/confirm?code=abc");

        await new SmtpEmailTransport(options, NullLogger<SmtpEmailTransport>.Instance).SendAsync(message, TestContext.Current.CancellationToken);

        using var mailpit = new HttpClient { BaseAddress = new Uri($"http://{_mailpit.Hostname}:{_mailpit.GetMappedPublicPort(ApiPort)}") };
        var list = await mailpit.GetFromJsonAsync<JsonElement>("/api/v1/messages", TestContext.Current.CancellationToken);
        var summary = Assert.Single(list.GetProperty("messages").EnumerateArray());
        Assert.Equal(message.Subject, summary.GetProperty("Subject").GetString());
        Assert.Equal("user@example.com", summary.GetProperty("To")[0].GetProperty("Address").GetString());

        var received = await mailpit.GetFromJsonAsync<JsonElement>($"/api/v1/message/{summary.GetProperty("ID").GetString()}", TestContext.Current.CancellationToken);
        Assert.Contains("https://api.example.com/confirm?code=abc", received.GetProperty("Text").GetString(), StringComparison.Ordinal);
        Assert.Contains("href=", received.GetProperty("HTML").GetString(), StringComparison.Ordinal);
    }
}
