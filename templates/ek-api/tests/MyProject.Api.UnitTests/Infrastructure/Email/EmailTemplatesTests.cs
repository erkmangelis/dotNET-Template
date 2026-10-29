using Microsoft.Extensions.Options;

namespace MyProject.Api.UnitTests.Infrastructure.Email;

public sealed class EmailTemplatesTests
{
    private readonly EmailTemplates _templates = new(Options.Create(new EmailOptions { FromName = "Acme" }));

    [Fact]
    public void A_confirmation_email_carries_the_link_in_both_bodies()
    {
        var message = _templates.ConfirmEmail("user@example.com", "https://api.example.com/confirm?code=abc&userId=1");

        Assert.Equal("user@example.com", message.To);
        Assert.Contains("Acme", message.Subject, StringComparison.Ordinal);
        Assert.Contains("https://api.example.com/confirm?code=abc&userId=1", message.TextBody, StringComparison.Ordinal);
        Assert.Contains("href=\"https://api.example.com/confirm?code=abc&amp;userId=1\"", message.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void Values_are_html_encoded()
    {
        var message = _templates.ResetPasswordCode("user@example.com", "<script>alert(1)</script>");

        Assert.DoesNotContain("<script>", message.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", message.HtmlBody, StringComparison.Ordinal);
    }
}
