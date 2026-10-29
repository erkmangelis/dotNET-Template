namespace Acme.Notes.Api.Infrastructure.Email;

/// <summary>
/// Development only: writes each message to the log instead of sending it, so you can follow
/// confirmation and reset links locally. Options validation stops it from running anywhere else.
/// </summary>
internal sealed partial class LogEmailTransport(ILogger<LogEmailTransport> logger) : IEmailTransport
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        LogMessage(logger, message.To, message.Subject, message.TextBody);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Email to {To} (development transport): {Subject}\n{Body}")]
    private static partial void LogMessage(ILogger logger, string to, string subject, string body);
}
