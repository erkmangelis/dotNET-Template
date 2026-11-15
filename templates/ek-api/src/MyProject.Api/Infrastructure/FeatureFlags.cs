#if (UseAuth)
using System.Security.Claims;
#endif
using Microsoft.FeatureManagement;
#if (UseAuth)
using Microsoft.FeatureManagement.FeatureFilters;
#endif

namespace MyProject.Api.Infrastructure;

/// <summary>
/// Flag names, read from the <c>FeatureManagement</c> section. Gate an endpoint with
/// <c>.WithFeatureGate(FeatureFlags.X)</c> (it answers 404 while the flag is off), or ask
/// <see cref="IFeatureManager"/> inside a handler. See docs/feature-flags.md.
/// </summary>
internal static class FeatureFlags
{
    /// <summary>
    /// <c>GET /notes/export</c>, which streams every matching note. On by default; an operations
    /// switch to turn off without a deploy if exports ever slow the database down.
    /// </summary>
    public const string NoteExport = "NoteExport";

    /// <summary>Flags from configuration, so environment variables and user secrets can switch them.</summary>
    public static WebApplicationBuilder AddFeatureFlags(this WebApplicationBuilder builder)
    {
#if (UseAuth)
        // Targeting turns a flag on for named users or roles, or a stable percentage of users.
        builder.Services.AddFeatureManagement().WithTargeting<UserTargetingContextAccessor>();
#else
        builder.Services.AddFeatureManagement();
#endif
        return builder;
    }
#if (UseAuth)

    /// <summary>Targeting by the signed-in user's id and roles. Anonymous callers have no id or groups.</summary>
    private sealed class UserTargetingContextAccessor(IHttpContextAccessor http) : ITargetingContextAccessor
    {
        public ValueTask<TargetingContext> GetContextAsync()
        {
            var user = http.HttpContext?.User;
            return ValueTask.FromResult(new TargetingContext
            {
                UserId = user?.FindFirstValue(ClaimTypes.NameIdentifier),
                Groups = user?.FindAll(ClaimTypes.Role).Select(role => role.Value).ToArray() ?? [],
            });
        }
    }
#endif
}
