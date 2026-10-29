using System.Text.Encodings.Web;
using Microsoft.Extensions.Options;

namespace Acme.Notes.Api.Infrastructure.Email;

/// <summary>
/// The messages the app sends. Each one renders a plain-text and an HTML body from the same parts,
/// and every value is HTML-encoded. Add a message as a new method here.
/// </summary>
internal sealed class EmailTemplates(IOptions<EmailOptions> options)
{
    private string AppName => options.Value.FromName;

    public EmailMessage ConfirmEmail(string to, string link) => Render(
        to,
        subject: $"Confirm your email for {AppName}",
        paragraphs: ["Confirm your email address to finish creating your account.", "If you did not sign up, ignore this email."],
        action: ("Confirm email", link));

    public EmailMessage ResetPasswordLink(string to, string link) => Render(
        to,
        subject: $"Reset your {AppName} password",
        paragraphs: ["Someone asked to reset the password for this account.", "If it was not you, ignore this email; your password stays the same."],
        action: ("Reset password", link));

    public EmailMessage ResetPasswordCode(string to, string code) => Render(
        to,
        subject: $"Your {AppName} password reset code",
        paragraphs: [$"Your password reset code is {code}.", "If you did not ask for it, ignore this email; your password stays the same."],
        action: null);

    private static EmailMessage Render(string to, string subject, string[] paragraphs, (string Label, string Url)? action)
    {
        var html = HtmlEncoder.Default;
        var text = string.Join("\n\n", paragraphs);
        var htmlParagraphs = string.Concat(paragraphs.Select(paragraph => $"<p>{html.Encode(paragraph)}</p>"));
        if (action is { } link)
        {
            text += $"\n\n{link.Label}: {link.Url}";
            htmlParagraphs += $"<p><a href=\"{html.Encode(link.Url)}\">{html.Encode(link.Label)}</a></p>";
        }

        var htmlBody = $"""
            <!doctype html>
            <html>
            <body style="font-family: sans-serif; line-height: 1.5; max-width: 560px">
            {htmlParagraphs}
            </body>
            </html>
            """;

        return new EmailMessage(to, subject, text, htmlBody);
    }
}
