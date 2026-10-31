#if (UseSmtp)
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace MyProject.Api.IntegrationTests;

/// <summary>Replaces the email transport and keeps every rendered message, so tests can follow the links.</summary>
public sealed partial class CapturedEmails : IEmailTransport
{
    private readonly ConcurrentQueue<EmailMessage> _messages = new();

    internal EmailMessage LastMessageTo(string email) =>
        _messages.Last(message => string.Equals(message.To, email, StringComparison.OrdinalIgnoreCase));

    internal bool Received(string email, string subject) =>
        _messages.Any(message => string.Equals(message.To, email, StringComparison.OrdinalIgnoreCase) && message.Subject == subject);

    internal string ConfirmationLinkFor(string email) =>
        new Uri(LinkPattern().Match(LastMessageTo(email).TextBody).Value).PathAndQuery;

    Task IEmailTransport.SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        _messages.Enqueue(message);
        return Task.CompletedTask;
    }

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex LinkPattern();
}
#else
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Identity;

namespace MyProject.Api.IntegrationTests;

/// <summary>Keeps the links Identity would email, so tests can follow them.</summary>
public sealed class CapturedEmails : IEmailSender<AppUser>
{
    private readonly ConcurrentDictionary<string, string> _confirmationLinks = new(StringComparer.OrdinalIgnoreCase);

    internal string ConfirmationLinkFor(string email) =>
        new Uri(System.Net.WebUtility.HtmlDecode(_confirmationLinks[email])).PathAndQuery;

    Task IEmailSender<AppUser>.SendConfirmationLinkAsync(AppUser user, string email, string confirmationLink)
    {
        _confirmationLinks[email] = confirmationLink;
        return Task.CompletedTask;
    }

    Task IEmailSender<AppUser>.SendPasswordResetLinkAsync(AppUser user, string email, string resetLink) => Task.CompletedTask;

    Task IEmailSender<AppUser>.SendPasswordResetCodeAsync(AppUser user, string email, string resetCode) => Task.CompletedTask;
}
#endif
