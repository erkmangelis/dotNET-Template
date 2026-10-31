using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Acme.Notes.Api.IntegrationTests;

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
