using Microsoft.Extensions.Options;

namespace Acme.Notes.Api.Infrastructure.Email;

internal static class EmailSetup
{
    /// <summary>
    /// Email through <see cref="IEmailTransport"/>: SMTP by default, or the log in development
    /// (<c>Email:Transport</c> = <c>Log</c>). Render messages with <see cref="EmailTemplates"/>.
    /// </summary>
    public static WebApplicationBuilder AddEmail(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<EmailOptions>()
            .BindConfiguration(EmailOptions.SectionName)
            .ValidateDataAnnotations()
            .Validate<IHostEnvironment>(
                EmailOptions.AreValid,
                "Email:Smtp:Host is required for the Smtp transport, and the Log transport only runs in Development.")
            .ValidateOnStart();

        builder.Services.AddSingleton<EmailTemplates>();
        builder.Services.AddSingleton<SmtpEmailTransport>();
        builder.Services.AddSingleton<LogEmailTransport>();
        builder.Services.AddSingleton<IEmailTransport>(services =>
            services.GetRequiredService<IOptions<EmailOptions>>().Value.Transport == EmailTransportKind.Log
                ? services.GetRequiredService<LogEmailTransport>()
                : services.GetRequiredService<SmtpEmailTransport>());

        return builder;
    }
}
