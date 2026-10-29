using System.ComponentModel.DataAnnotations;

namespace Acme.Notes.Api.Data;

/// <summary>Database settings, bound from the <c>Database</c> section and <c>ConnectionStrings:Default</c>.</summary>
internal sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>
    /// Read from <c>ConnectionStrings:Default</c>. Keep it out of appsettings files: use user secrets
    /// in development and an environment variable (<c>ConnectionStrings__Default</c>) everywhere else.
    /// </summary>
    [Required(ErrorMessage = "Set ConnectionStrings:Default with user secrets in development or the ConnectionStrings__Default environment variable.")]
    public string? ConnectionString { get; set; }

    /// <summary>Apply pending migrations when the app starts. Meant for development; production runs the migration bundle.</summary>
    public bool MigrateOnStartup { get; set; }

    /// <summary>Add sample data in the Development environment. Ignored in every other environment.</summary>
    public bool SeedDevelopmentData { get; set; }
}
