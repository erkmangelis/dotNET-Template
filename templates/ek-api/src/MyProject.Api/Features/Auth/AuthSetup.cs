using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;

namespace MyProject.Api.Features.Auth;

internal static class AuthSetup
{
    /// <summary>
    /// ASP.NET Core Identity with its API endpoints and bearer tokens. Defaults are set here; the
    /// <c>Identity</c> configuration section can override any <see cref="IdentityOptions"/> value.
    /// </summary>
    public static WebApplicationBuilder AddIdentityAuth(this WebApplicationBuilder builder)
    {
        var services = builder.Services;

        if (builder.Environment.IsDevelopment())
        {
            services.AddSingleton<IEmailSender<AppUser>, DevelopmentEmailSender>();
        }
        else
        {
            services.AddSingleton<IEmailSender<AppUser>, UnconfiguredEmailSender>();
        }

        services.AddAuthorization();
        services.AddIdentityApiEndpoints<AppUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.SignIn.RequireConfirmedEmail = true;
                options.Password.RequiredLength = 12;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddEntityFrameworkStores<AppDbContext>();
        services.AddOptions<IdentityOptions>().BindConfiguration("Identity");

        services.Configure<BearerTokenOptions>(IdentityConstants.BearerScheme, options =>
        {
            options.BearerTokenExpiration = TimeSpan.FromMinutes(60);
            options.RefreshTokenExpiration = TimeSpan.FromDays(14);
        });

        services.AddDataProtection()
            .SetApplicationName("MyProject")
            .PersistKeysToDbContext<AppDbContext>();

        return builder;
    }

    /// <summary>Register, login, refresh, confirm email, reset password and manage the account, under <c>/v1/auth</c>.</summary>
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGroup("/auth").WithTags("Auth").MapIdentityApi<AppUser>();
        return routes;
    }

    public static string GetUserId(this ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("The endpoint must require an authenticated user.");
}
