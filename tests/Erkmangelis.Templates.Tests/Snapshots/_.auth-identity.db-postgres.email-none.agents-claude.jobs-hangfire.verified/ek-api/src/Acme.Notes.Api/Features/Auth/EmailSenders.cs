using Microsoft.AspNetCore.Identity;

namespace Acme.Notes.Api.Features.Auth;

/// <summary>
/// Development only: writes confirmation and reset links to the log instead of sending email,
/// so you can finish the flows locally. The links are secrets, which is why this never runs elsewhere.
/// </summary>
internal sealed partial class DevelopmentEmailSender(ILogger<DevelopmentEmailSender> logger) : IEmailSender<AppUser>
{
    public Task SendConfirmationLinkAsync(AppUser user, string email, string confirmationLink)
    {
        LogLink(logger, "Email confirmation", user.Id, confirmationLink);
        return Task.CompletedTask;
    }

    public Task SendPasswordResetLinkAsync(AppUser user, string email, string resetLink)
    {
        LogLink(logger, "Password reset", user.Id, resetLink);
        return Task.CompletedTask;
    }

    public Task SendPasswordResetCodeAsync(AppUser user, string email, string resetCode)
    {
        LogLink(logger, "Password reset code", user.Id, resetCode);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Purpose} for user {UserId} (development email sender): {Link}")]
    private static partial void LogLink(ILogger logger, string purpose, string userId, string link);
}

/// <summary>
/// Used outside development until a real email provider is added. It logs that a message was
/// dropped, never the link itself.
/// </summary>
internal sealed partial class UnconfiguredEmailSender(ILogger<UnconfiguredEmailSender> logger) : IEmailSender<AppUser>
{
    public Task SendConfirmationLinkAsync(AppUser user, string email, string confirmationLink) => Drop("Email confirmation", user);

    public Task SendPasswordResetLinkAsync(AppUser user, string email, string resetLink) => Drop("Password reset", user);

    public Task SendPasswordResetCodeAsync(AppUser user, string email, string resetCode) => Drop("Password reset code", user);

    private Task Drop(string purpose, AppUser user)
    {
        LogDropped(logger, purpose, user.Id);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Purpose} for user {UserId} was not sent: no email provider is configured")]
    private static partial void LogDropped(ILogger logger, string purpose, string userId);
}
