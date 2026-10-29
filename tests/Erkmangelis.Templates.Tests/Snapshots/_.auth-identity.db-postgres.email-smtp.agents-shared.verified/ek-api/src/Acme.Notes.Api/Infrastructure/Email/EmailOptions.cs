using System.ComponentModel.DataAnnotations;
using MailKit.Security;

namespace Acme.Notes.Api.Infrastructure.Email;

internal enum EmailTransportKind
{
    /// <summary>Writes messages to the log. Development and tests only, because messages hold secret links.</summary>
    Log,

    /// <summary>Sends through an SMTP server, such as your provider's relay.</summary>
    Smtp,
}

/// <summary>Email settings, bound from the <c>Email</c> section.</summary>
internal sealed class EmailOptions
{
    public const string SectionName = "Email";

    public EmailTransportKind Transport { get; set; } = EmailTransportKind.Smtp;

    /// <summary>The sender address, for example <c>noreply@example.com</c>. Your provider must allow it.</summary>
    [Required]
    [EmailAddress]
    public string FromAddress { get; set; } = "";

    [MaxLength(100)]
    public string FromName { get; set; } = "Acme.Notes";

    public SmtpSettings Smtp { get; set; } = new();

    internal static bool AreValid(EmailOptions options, IHostEnvironment environment) =>
        options.Transport switch
        {
            EmailTransportKind.Log => environment.IsDevelopment() || environment.IsEnvironment("Testing"),
            EmailTransportKind.Smtp => !string.IsNullOrWhiteSpace(options.Smtp.Host),
            _ => false,
        };
}

internal sealed class SmtpSettings
{
    public string Host { get; set; } = "";

    [Range(1, 65535)]
    public int Port { get; set; } = 587;

    /// <summary>
    /// <c>StartTls</c> for port 587 and <c>SslOnConnect</c> for 465. <c>None</c> only for a relay on a
    /// private network or a local test server.
    /// </summary>
    public SecureSocketOptions Security { get; set; } = SecureSocketOptions.StartTls;

    /// <summary>Leave empty when the server needs no sign-in. Set the password through an environment variable.</summary>
    public string? UserName { get; set; }

    public string? Password { get; set; }

    [Range(1, 300)]
    public int TimeoutSeconds { get; set; } = 30;
}
