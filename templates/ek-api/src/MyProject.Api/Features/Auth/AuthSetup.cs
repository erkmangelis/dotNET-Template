using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;

namespace MyProject.Api.Features.Auth;

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

#if (UseSmtp)
        services.AddSingleton<IEmailSender<AppUser>, IdentityEmailSender>();
#else
        if (builder.Environment.IsDevelopment())
        {
            services.AddSingleton<IEmailSender<AppUser>, DevelopmentEmailSender>();
        }
        else
        {
            services.AddSingleton<IEmailSender<AppUser>, UnconfiguredEmailSender>();
        }
#endif

        services.AddAuthorization();
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
            .AddEntityFrameworkStores<AppDbContext>();
        services.AddOptions<IdentityOptions>().BindConfiguration("Identity");

        services.AddBrowserSessions();
        services.Configure<BearerTokenOptions>(IdentityConstants.BearerScheme, options =>
        {
            options.BearerTokenExpiration = TimeSpan.FromMinutes(60);
            options.RefreshTokenExpiration = TimeSpan.FromDays(14);
        });

        services.AddDataProtection()
            .SetApplicationName("MyProject")
            .PersistKeysToDbContext<AppDbContext>();
#if (UseExternal)

        services.AddOptions<ExternalAuthOptions>()
            .BindConfiguration(ExternalAuthOptions.SectionName)
            .Validate(ExternalAuthOptions.AreValid, "Every ExternalAuth provider needs an https Authority and an EmailVerifiedClaim.")
            .ValidateOnStart();
        services.AddHttpClient(nameof(ExternalProviderMetadata));
        services.AddSingleton<IExternalProviderMetadata, ExternalProviderMetadata>();
        services.AddSingleton<ExternalTokenValidator>();
#endif

        return builder;
    }

    /// <summary>Register, login, refresh, confirm email, reset password and manage the account, under <c>/v1/auth</c>.</summary>
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        var auth = routes.MapGroup("/auth").WithTags("Auth");
        auth.MapIdentityApi<AppUser>();
        auth.MapBrowserSessionEndpoints();
#if (UseExternal)
        auth.MapExternalAuthEndpoints();
#endif
        return routes;
    }
}
