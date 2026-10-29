using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;

namespace MyProject.Api.Features.Auth;

public sealed record PasskeyRegistrationRequest
{
    /// <summary>The result of <c>navigator.credentials.create()</c>, as <c>PublicKeyCredential.toJSON()</c> gives it.</summary>
    [Required]
    public JsonElement? Credential { get; init; }

    [StringLength(100)]
    public string? Name { get; init; }
}

public sealed record PasskeySignInOptionsRequest
{
    /// <summary>Optional. Without it, the browser offers every passkey it holds for this site.</summary>
    [EmailAddress]
    [StringLength(256)]
    public string? Email { get; init; }
}

public sealed record PasskeySignInRequest
{
    /// <summary>The result of <c>navigator.credentials.get()</c>, as <c>PublicKeyCredential.toJSON()</c> gives it.</summary>
    [Required]
    public JsonElement? Credential { get; init; }
}

public sealed record PasskeyResponse(string Id, string? Name, DateTimeOffset CreatedAt, bool IsBackedUp);

/// <summary>
/// Passkeys (WebAuthn) with ASP.NET Core Identity. Each ceremony is two calls: get options, run them in
/// the browser, post the result. Identity keeps the challenge in a short-lived cookie between the two,
/// so the client must send cookies on both calls. A passkey sign-in counts as two factors.
/// </summary>
internal static class Passkeys
{
    public static IServiceCollection AddPasskeys(this IServiceCollection services)
    {
        // ServerDomain is the WebAuthn relying party id. With a frontend on app.example.com and the API on
        // api.example.com, set it to example.com; it defaults to the API's host.
        services.AddOptions<IdentityPasskeyOptions>().BindConfiguration("Passkeys");

        // The cookie that carries the ceremony state between the two calls.
        services.Configure<CookieAuthenticationOptions>(IdentityConstants.TwoFactorUserIdScheme, options =>
        {
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });

        return services;
    }

    public static IEndpointRouteBuilder MapPasskeyEndpoints(this IEndpointRouteBuilder auth)
    {
        var passkeys = auth.MapGroup("/passkeys");

        passkeys.MapPost("/creation-options", CreationOptionsAsync).RequireAuthorization()
            .WithSummary("Start adding a passkey: options for navigator.credentials.create()");
        passkeys.MapPost("/", RegisterAsync).RequireAuthorization()
            .WithSummary("Finish adding a passkey");
        passkeys.MapGet("/", ListAsync).RequireAuthorization()
            .WithSummary("List the signed-in user's passkeys");
        passkeys.MapDelete("/{id}", DeleteAsync).RequireAuthorization()
            .WithSummary("Remove a passkey");
        passkeys.MapPost("/request-options", RequestOptionsAsync)
            .WithSummary("Start a passkey sign-in: options for navigator.credentials.get()");
        passkeys.MapPost("/login", SignInAsync)
            .WithSummary("Finish a passkey sign-in; answers like /login");

        return auth;
    }

    private static async Task<Results<ContentHttpResult, NotFound>> CreationOptionsAsync(
        ClaimsPrincipal principal,
        UserManager<AppUser> users,
        SignInManager<AppUser> signIn)
    {
        if (await users.GetUserAsync(principal) is not { } user)
        {
            return TypedResults.NotFound();
        }

        var name = user.Email ?? user.UserName ?? user.Id;
        var options = await signIn.MakePasskeyCreationOptionsAsync(new PasskeyUserEntity { Id = user.Id, Name = name, DisplayName = name });
        return TypedResults.Text(options, "application/json");
    }

    private static async Task<Results<Created<PasskeyResponse>, NotFound, ProblemHttpResult>> RegisterAsync(
        PasskeyRegistrationRequest request,
        ClaimsPrincipal principal,
        UserManager<AppUser> users,
        SignInManager<AppUser> signIn)
    {
        if (await users.GetUserAsync(principal) is not { } user)
        {
            return TypedResults.NotFound();
        }

        PasskeyAttestationResult attestation;
        try
        {
            attestation = await signIn.PerformPasskeyAttestationAsync(request.Credential!.Value.GetRawText());
        }
        catch (InvalidOperationException)
        {
            return InvalidPasskey("Start again with /v1/auth/passkeys/creation-options; no passkey registration is under way.");
        }

        if (!attestation.Succeeded || attestation.UserEntity.Id != user.Id)
        {
            return InvalidPasskey(attestation.Failure?.Message);
        }

        var passkey = attestation.Passkey;
        passkey.Name = request.Name;
        var added = await users.AddOrUpdatePasskeyAsync(user, passkey);
        if (!added.Succeeded)
        {
            return InvalidPasskey(added.Errors.FirstOrDefault()?.Description);
        }

        var response = ToResponse(passkey);
        return TypedResults.Created($"/v1/auth/passkeys/{response.Id}", response);
    }

    private static async Task<Results<Ok<PasskeyResponse[]>, NotFound>> ListAsync(ClaimsPrincipal principal, UserManager<AppUser> users)
    {
        if (await users.GetUserAsync(principal) is not { } user)
        {
            return TypedResults.NotFound();
        }

        var passkeys = await users.GetPasskeysAsync(user);
        return TypedResults.Ok(passkeys.Select(ToResponse).ToArray());
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> DeleteAsync(
        string id,
        ClaimsPrincipal principal,
        UserManager<AppUser> users)
    {
        if (await users.GetUserAsync(principal) is not { } user || !TryDecode(id, out var credentialId))
        {
            return TypedResults.NotFound();
        }

        var passkeys = await users.GetPasskeysAsync(user);
        if (!passkeys.Any(passkey => passkey.CredentialId.AsSpan().SequenceEqual(credentialId)))
        {
            return TypedResults.NotFound();
        }

        if (passkeys.Count == 1 && !await users.HasPasswordAsync(user) && (await users.GetLoginsAsync(user)).Count == 0)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Last sign-in method",
                detail: "Set a password or add another passkey before you remove this one.");
        }

        await users.RemovePasskeyAsync(user, credentialId);
        return TypedResults.NoContent();
    }

    private static async Task<ContentHttpResult> RequestOptionsAsync(
        PasskeySignInOptionsRequest request,
        UserManager<AppUser> users,
        SignInManager<AppUser> signIn)
    {
        // An unknown email gets the same answer as no email, so the endpoint cannot tell who has an account.
        var user = request.Email is null ? null : await users.FindByEmailAsync(request.Email);
        var options = await signIn.MakePasskeyRequestOptionsAsync(user);
        return TypedResults.Text(options, "application/json");
    }

    private static async Task<Results<Ok<AccessTokenResponse>, EmptyHttpResult, ProblemHttpResult>> SignInAsync(
        PasskeySignInRequest request,
        SignInManager<AppUser> signIn,
        bool? useCookies = null)
    {
        signIn.AuthenticationScheme = useCookies == true ? IdentityConstants.ApplicationScheme : IdentityConstants.BearerScheme;

        SignInResult result;
        try
        {
            result = await signIn.PasskeySignInAsync(request.Credential!.Value.GetRawText());
        }
        catch (InvalidOperationException)
        {
            result = SignInResult.Failed;
        }

        if (!result.Succeeded)
        {
            return TypedResults.Problem(result.ToString(), statusCode: StatusCodes.Status401Unauthorized);
        }

        // The sign-in has already written the token response or the (session) cookie.
        return TypedResults.Empty;
    }

    private static PasskeyResponse ToResponse(UserPasskeyInfo passkey) =>
        new(WebEncoders.Base64UrlEncode(passkey.CredentialId), passkey.Name, passkey.CreatedAt, passkey.IsBackedUp);

    private static bool TryDecode(string id, out byte[] credentialId)
    {
        try
        {
            credentialId = WebEncoders.Base64UrlDecode(id);
            return true;
        }
        catch (FormatException)
        {
            credentialId = [];
            return false;
        }
    }

    private static ProblemHttpResult InvalidPasskey(string? detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid passkey", detail: detail);
}
