namespace Acme.Notes.Api.Infrastructure.Email;

/// <summary>One email, already rendered. Build it with <see cref="EmailTemplates"/>.</summary>
internal sealed record EmailMessage(string To, string Subject, string TextBody, string HtmlBody);

/// <summary>Sends rendered email. <see cref="SmtpEmailTransport"/> in production, <see cref="LogEmailTransport"/> in development.</summary>
internal interface IEmailTransport
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
