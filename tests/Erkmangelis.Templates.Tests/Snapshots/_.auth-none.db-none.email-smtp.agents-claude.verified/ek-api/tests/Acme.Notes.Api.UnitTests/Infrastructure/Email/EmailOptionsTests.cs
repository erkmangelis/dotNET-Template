using Microsoft.Extensions.Hosting.Internal;

namespace Acme.Notes.Api.UnitTests.Infrastructure.Email;

public sealed class EmailOptionsTests
{
    [Theory]
    [InlineData("Development", true)]
    [InlineData("Testing", true)]
    [InlineData("Production", false)]
    [InlineData("Staging", false)]
    public void The_log_transport_runs_only_in_development_and_tests(string environment, bool valid) =>
        Assert.Equal(valid, EmailOptions.AreValid(
            new EmailOptions { Transport = EmailTransportKind.Log },
            new HostingEnvironment { EnvironmentName = environment }));

    [Theory]
    [InlineData("", false)]
    [InlineData("smtp.example.com", true)]
    public void The_smtp_transport_needs_a_host(string host, bool valid) =>
        Assert.Equal(valid, EmailOptions.AreValid(
            new EmailOptions { Transport = EmailTransportKind.Smtp, Smtp = new SmtpSettings { Host = host } },
            new HostingEnvironment { EnvironmentName = "Production" }));
}
