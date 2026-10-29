using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;

namespace MyProject.Api.Features.Auth;

public sealed record AntiforgeryTokenResponse(string Token, string HeaderName);

/// <summary>
/// Cookie sessions for a browser frontend on the same site, such as a Next.js app on
/// <c>app.example.com</c> calling <c>api.example.com</c>. Sign in with <c>?useCookies=true</c>. The
/// cookie is HttpOnly, Secure and SameSite=Strict, and every unsafe request made with it must carry
/// an antiforgery token in the <see cref="HeaderName"/> header.
/// </summary>
internal static class BrowserSessions
{
    public const string HeaderName = "X-CSRF-TOKEN";

    public static IServiceCollection AddBrowserSessions(this IServiceCollection services)
    {
        services.ConfigureApplicationCookie(options =>
        {
            // __Host- cookies are Secure, have no Domain and cover the whole host, so nothing else can set or widen them.
            options.Cookie.Name = "__Host-MyProject";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.ExpireTimeSpan = TimeSpan.FromDays(14);
            options.SlidingExpiration = true;
        });

        services.AddAntiforgery(options =>
        {
            options.HeaderName = HeaderName;
            options.Cookie.Name = "__Host-MyProject-csrf";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });

        return services;
    }

    /// <summary>Refuses unsafe requests that the session cookie authenticated unless they carry a valid antiforgery token.</summary>
    public static IApplicationBuilder UseBrowserSessionAntiforgery(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (IsUnsafe(context.Request.Method) && IsCookieSession(context))
            {
                var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
                if (!await antiforgery.IsRequestValidAsync(context))
                {
                    await Results.Problem(
                            statusCode: StatusCodes.Status400BadRequest,
                            title: "Missing antiforgery token",
                            detail: $"Requests signed in with the session cookie must send the token from GET /v1/auth/antiforgery in the {HeaderName} header.")
                        .ExecuteAsync(context);
                    return;
                }
            }

            await next(context);
        });

    public static IEndpointRouteBuilder MapBrowserSessionEndpoints(this IEndpointRouteBuilder auth)
    {
        auth.MapGet("/antiforgery", GetAntiforgeryToken)
            .WithSummary("Get the antiforgery token for cookie sessions; call it again after signing in");
        auth.MapPost("/logout", LogoutAsync)
            .RequireAuthorization()
            .WithSummary("End the cookie session");

        return auth;
    }

    private static Ok<AntiforgeryTokenResponse> GetAntiforgeryToken(HttpContext context, IAntiforgery antiforgery) =>
        TypedResults.Ok(new AntiforgeryTokenResponse(antiforgery.GetAndStoreTokens(context).RequestToken!, HeaderName));

    private static async Task<NoContent> LogoutAsync(SignInManager<AppUser> signIn)
    {
        await signIn.SignOutAsync();
        return TypedResults.NoContent();
    }

    private static bool IsUnsafe(string method) =>
        !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method));

    private static bool IsCookieSession(HttpContext context) =>
        context.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult?.Ticket?.AuthenticationScheme == IdentityConstants.ApplicationScheme;
}
