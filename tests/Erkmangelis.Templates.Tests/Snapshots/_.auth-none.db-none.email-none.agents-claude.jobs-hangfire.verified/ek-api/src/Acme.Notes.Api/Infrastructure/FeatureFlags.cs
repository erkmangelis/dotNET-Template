using Microsoft.FeatureManagement;

namespace Acme.Notes.Api.Infrastructure;

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
        builder.Services.AddFeatureManagement();
        return builder;
    }
}
