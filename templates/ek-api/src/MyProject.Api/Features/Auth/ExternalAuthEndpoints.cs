using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;

namespace MyProject.Api.Features.Auth;

/// <summary>An ID token the app got from the provider's own sign-in (Google Identity Services, Sign in with Apple, MSAL).</summary>
public sealed record ExternalSignInRequest
{
    [Required]
    [StringLength(16_384)]
    public string IdToken { get; init; } = string.Empty;

    /// <summary>The nonce the app gave the provider, when it used one.</summary>
    [StringLength(512)]
    public string? Nonce { get; init; }

    [StringLength(16)]
    public string? TwoFactorCode { get; init; }

    [StringLength(64)]
    public string? TwoFactorRecoveryCode { get; init; }
}

public sealed record ExternalLinkRequest
{
    [Required]
    [StringLength(16_384)]
    public string IdToken { get; init; } = string.Empty;

    [StringLength(512)]
    public string? Nonce { get; init; }
}

public sealed record ExternalProvidersResponse(IReadOnlyList<string> Providers);

/// <summary>
/// Sign-in with Google, Apple, Microsoft or any OpenID Connect provider. The client signs in with the
/// provider, sends the ID token here, and gets the same bearer token or cookie as a password sign-in.
/// A provider's login is linked to an existing account only when the provider says the email is verified.
/// </summary>
internal static partial class ExternalAuthEndpoints
{
    public static IEndpointRouteBuilder MapExternalAuthEndpoints(this IEndpointRouteBuilder auth)
    {
        var external = auth.MapGroup("/external");

        external.MapGet("/", ListProviders).WithSummary("List the sign-in providers that are turned on");
        external.MapPost("/{provider}", SignInAsync).WithSummary("Sign in with a provider's ID token");
        external.MapPost("/{provider}/link", LinkAsync).RequireAuthorization().WithSummary("Link a provider to the signed-in account");
        external.MapDelete("/{provider}", UnlinkAsync).RequireAuthorization().WithSummary("Unlink a provider from the signed-in account");

        return auth;
    }

    private static Ok<ExternalProvidersResponse> ListProviders(ExternalTokenValidator validator) =>
        TypedResults.Ok(new ExternalProvidersResponse([.. validator.EnabledProviders]));

    private static async Task<Results<Ok<AccessTokenResponse>, EmptyHttpResult, NotFound, ProblemHttpResult, ValidationProblem>> SignInAsync(
        string provider,
        ExternalSignInRequest request,
        ExternalTokenValidator validator,
        UserManager<AppUser> users,
        SignInManager<AppUser> signIn,
        ApiKeyService apiKeys,
        ILoggerFactory loggers,
        CancellationToken cancellationToken,
        bool? useCookies = null,
        bool? useSessionCookies = null)
    {
        if (validator.FindProvider(provider) is not var (name, options))
        {
            return TypedResults.NotFound();
        }

        var identity = await validator.ValidateAsync(name, options, request.IdToken, request.Nonce, cancellationToken);
        if (identity is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid ID token");
        }

        if (await users.FindByLoginAsync(name, identity.Subject) is null)
        {
            // A new login. Only an email the provider verified may create an account or join an existing one;
            // otherwise anyone could add an unverified address at the provider and take over that account.
            var logger = loggers.CreateLogger(typeof(ExternalAuthEndpoints));
            if (!identity.EmailVerified || identity.Email is null)
            {
                LogUnverifiedEmailRefused(logger, name);
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Email not verified",
                    detail: "The provider did not confirm this email address. Sign in another way, then link this provider to your account.");
            }

            var user = await users.FindByEmailAsync(identity.Email);
            if (user is null)
            {
                user = new AppUser { UserName = identity.Email, Email = identity.Email, EmailConfirmed = true };
                var created = await users.CreateAsync(user);
                if (!created.Succeeded)
                {
                    return ValidationProblem(created);
                }
            }
            else if (!user.EmailConfirmed)
            {
                // Nobody ever proved they own this unconfirmed account. The verified email wins, so whatever
                // credentials the account had are removed before the provider is linked.
                await ResetUnprovenAccountAsync(users, apiKeys, user, cancellationToken);
                LogUnconfirmedAccountClaimed(logger, name, user.Id);
            }

            var linked = await users.AddLoginAsync(user, new UserLoginInfo(name, identity.Subject, name));
            if (!linked.Succeeded)
            {
                return ValidationProblem(linked);
            }
        }

        signIn.AuthenticationScheme = useCookies == true ? IdentityConstants.ApplicationScheme : IdentityConstants.BearerScheme;
        var isPersistent = useCookies == true && useSessionCookies != true;

        var result = await signIn.ExternalLoginSignInAsync(name, identity.Subject, isPersistent, bypassTwoFactor: false);
        if (result.RequiresTwoFactor)
        {
            if (!string.IsNullOrEmpty(request.TwoFactorCode))
            {
                result = await signIn.TwoFactorAuthenticatorSignInAsync(request.TwoFactorCode, isPersistent, rememberClient: isPersistent);
            }
            else if (!string.IsNullOrEmpty(request.TwoFactorRecoveryCode))
            {
                result = await signIn.TwoFactorRecoveryCodeSignInAsync(request.TwoFactorRecoveryCode);
            }
        }

        if (!result.Succeeded)
        {
            return TypedResults.Problem(result.ToString(), statusCode: StatusCodes.Status401Unauthorized);
        }

        // The sign-in has already written the token response or the cookie.
        return TypedResults.Empty;
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult, ValidationProblem>> LinkAsync(
        string provider,
        ExternalLinkRequest request,
        ClaimsPrincipal principal,
        ExternalTokenValidator validator,
        UserManager<AppUser> users,
        CancellationToken cancellationToken)
    {
        if (validator.FindProvider(provider) is not var (name, options))
        {
            return TypedResults.NotFound();
        }

        // The caller proved they own the account, so the provider's email does not matter here.
        var identity = await validator.ValidateAsync(name, options, request.IdToken, request.Nonce, cancellationToken);
        var user = await users.GetUserAsync(principal);
        if (identity is null || user is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid ID token");
        }

        var owner = await users.FindByLoginAsync(name, identity.Subject);
        if (owner is not null)
        {
            return owner.Id == user.Id
                ? TypedResults.NoContent()
                : TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Already linked", detail: "This login is linked to another account.");
        }

        var linked = await users.AddLoginAsync(user, new UserLoginInfo(name, identity.Subject, name));
        return linked.Succeeded ? TypedResults.NoContent() : ValidationProblem(linked);
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> UnlinkAsync(
        string provider,
        ClaimsPrincipal principal,
        UserManager<AppUser> users)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null)
        {
            return TypedResults.NotFound();
        }

        var logins = await users.GetLoginsAsync(user);
        var login = logins.FirstOrDefault(existing => string.Equals(existing.LoginProvider, provider, StringComparison.OrdinalIgnoreCase));
        if (login is null)
        {
            return TypedResults.NotFound();
        }

        if (logins.Count == 1 && !await users.HasPasswordAsync(user) && (await users.GetPasskeysAsync(user)).Count == 0)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Last sign-in method",
                detail: "Set a password, add a passkey or link another provider before you unlink this one.");
        }

        await users.RemoveLoginAsync(user, login.LoginProvider, login.ProviderKey);
        return TypedResults.NoContent();
    }

    /// <summary>Removes every way in to an account whose email was never confirmed, and signs out its sessions.</summary>
    private static async Task ResetUnprovenAccountAsync(UserManager<AppUser> users, ApiKeyService apiKeys, AppUser user, CancellationToken cancellationToken)
    {
        if (await users.HasPasswordAsync(user))
        {
            await users.RemovePasswordAsync(user);
        }

        foreach (var login in await users.GetLoginsAsync(user))
        {
            await users.RemoveLoginAsync(user, login.LoginProvider, login.ProviderKey);
        }

        foreach (var passkey in await users.GetPasskeysAsync(user))
        {
            await users.RemovePasskeyAsync(user, passkey.CredentialId);
        }

        await apiKeys.RevokeAllAsync(user.Id, cancellationToken);
        await users.SetTwoFactorEnabledAsync(user, false);
        await users.ResetAuthenticatorKeyAsync(user);
        user.EmailConfirmed = true;
        await users.UpdateAsync(user);
        await users.UpdateSecurityStampAsync(user);
    }

    private static ValidationProblem ValidationProblem(IdentityResult result) =>
        TypedResults.ValidationProblem(result.Errors
            .GroupBy(error => error.Code)
            .ToDictionary(group => group.Key, group => group.Select(error => error.Description).ToArray()));

    [LoggerMessage(Level = LogLevel.Warning, Message = "External sign-in with {Provider} refused: the provider did not verify the email")]
    private static partial void LogUnverifiedEmailRefused(ILogger logger, string provider);

    [LoggerMessage(Level = LogLevel.Warning, Message = "External sign-in with {Provider} took over unconfirmed account {UserId}; its old credentials were removed")]
    private static partial void LogUnconfirmedAccountClaimed(ILogger logger, string provider, string userId);
}
