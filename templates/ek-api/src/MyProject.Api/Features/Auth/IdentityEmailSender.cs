using System.Net;
using Microsoft.AspNetCore.Identity;

namespace MyProject.Api.Features.Auth;

/// <summary>Sends Identity's account emails through the app's email templates and transport.</summary>
internal sealed class IdentityEmailSender(EmailTemplates templates, IEmailTransport transport) : IEmailSender<AppUser>
{
    // Identity hands over links that are already HTML-encoded; the templates encode them again for HTML.
    public Task SendConfirmationLinkAsync(AppUser user, string email, string confirmationLink) =>
        transport.SendAsync(templates.ConfirmEmail(email, WebUtility.HtmlDecode(confirmationLink)), CancellationToken.None);

    public Task SendPasswordResetLinkAsync(AppUser user, string email, string resetLink) =>
        transport.SendAsync(templates.ResetPasswordLink(email, WebUtility.HtmlDecode(resetLink)), CancellationToken.None);

    public Task SendPasswordResetCodeAsync(AppUser user, string email, string resetCode) =>
        transport.SendAsync(templates.ResetPasswordCode(email, WebUtility.HtmlDecode(resetCode)), CancellationToken.None);
}
