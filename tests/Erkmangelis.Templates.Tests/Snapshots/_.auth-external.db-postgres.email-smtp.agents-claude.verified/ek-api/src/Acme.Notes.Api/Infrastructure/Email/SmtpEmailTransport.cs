using MailKit.Net.Smtp;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Acme.Notes.Api.Infrastructure.Email;

/// <summary>Sends each message over its own SMTP connection with MailKit.</summary>
internal sealed partial class SmtpEmailTransport(IOptions<EmailOptions> options, ILogger<SmtpEmailTransport> logger) : IEmailTransport
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody }.ToMessageBody();

        using var client = new SmtpClient { Timeout = settings.Smtp.TimeoutSeconds * 1000 };
        await client.ConnectAsync(settings.Smtp.Host, settings.Smtp.Port, settings.Smtp.Security, cancellationToken);
        if (!string.IsNullOrEmpty(settings.Smtp.UserName))
        {
            await client.AuthenticateAsync(settings.Smtp.UserName, settings.Smtp.Password ?? "", cancellationToken);
        }

        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
        LogSent(logger, message.Subject);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sent email \"{Subject}\"")]
    private static partial void LogSent(ILogger logger, string subject);
}
