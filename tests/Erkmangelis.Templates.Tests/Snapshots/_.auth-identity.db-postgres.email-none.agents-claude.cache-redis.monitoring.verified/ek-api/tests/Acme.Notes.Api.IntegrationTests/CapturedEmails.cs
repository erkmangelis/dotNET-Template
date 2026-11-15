using System.Collections.Concurrent;
using Microsoft.AspNetCore.Identity;

namespace Acme.Notes.Api.IntegrationTests;

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
