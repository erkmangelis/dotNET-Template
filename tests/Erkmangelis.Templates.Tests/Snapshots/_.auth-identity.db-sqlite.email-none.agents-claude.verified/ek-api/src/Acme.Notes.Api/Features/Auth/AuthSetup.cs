using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;

namespace Acme.Notes.Api.Features.Auth;

internal static class AuthSetup
{
    /// <summary>Identity's table layout. Version 3 adds passkeys. The design-time context reads it from here too.</summary>
    public static readonly Version SchemaVersion = IdentitySchemaVersions.Version3;

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
        services.AddTransient<IClaimsTransformation, PermissionClaims>();
        services.AddIdentityApiEndpoints<AppUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.SignIn.RequireConfirmedEmail = true;
                options.Password.RequiredLength = 12;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Stores.SchemaVersion = SchemaVersion;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<AppDbContext>();
        services.AddOptions<IdentityOptions>().BindConfiguration("Identity");

        services.AddBrowserSessions();
        services.AddPasskeys();
        services.Configure<BearerTokenOptions>(IdentityConstants.BearerScheme, options =>
        {
            options.BearerTokenExpiration = TimeSpan.FromMinutes(60);
            options.RefreshTokenExpiration = TimeSpan.FromDays(14);
        });

        services.AddDataProtection()
            .SetApplicationName("Acme.Notes")
            .PersistKeysToDbContext<AppDbContext>();

        return builder;
    }

    /// <summary>Register, login, refresh, confirm email, reset password and manage the account, under <c>/v1/auth</c>.</summary>
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        var auth = routes.MapGroup("/auth").WithTags("Auth");
        auth.MapIdentityApi<AppUser>();
        auth.MapBrowserSessionEndpoints();
        auth.MapPasskeyEndpoints();
        return routes;
    }
}
