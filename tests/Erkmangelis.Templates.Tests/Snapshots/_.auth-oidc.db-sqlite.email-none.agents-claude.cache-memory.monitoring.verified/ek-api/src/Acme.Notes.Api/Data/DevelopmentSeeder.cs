using Microsoft.EntityFrameworkCore;

namespace Acme.Notes.Api.Data;

/// <summary>Sample data for local development. Never runs outside the Development environment.</summary>
internal sealed partial class DevelopmentSeeder(
    AppDbContext db,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<DevelopmentSeeder> logger)
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (await db.Notes.IgnoreQueryFilters([SoftDelete.Filter]).AnyAsync(cancellationToken))
        {
            return;
        }

        // Accounts live in the provider, so the notes go to the subject named in configuration. The
        // default is the dev user in keycloak/realm.json; for another provider set your own sub:
        // dotnet user-secrets set DevelopmentSeed:Subject "<your sub claim>"
        var ownerId = configuration["DevelopmentSeed:Subject"];
        if (string.IsNullOrEmpty(ownerId))
        {
            LogSeedSkipped(logger);
            return;
        }

        var now = time.GetUtcNow();
        string[] titles = ["Welcome", "Try the API reference at /scalar", "Delete this seed data when you start"];
        for (var i = 0; i < titles.Length; i++)
        {
            var createdAt = now.AddMinutes(-i);
            db.Notes.Add(new Note
            {
                Id = Guid.CreateVersion7(createdAt),
                OwnerId = ownerId,
                Title = titles[i],
                Content = "Development seed data.",
                CreatedAt = createdAt,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Development seed skipped: set DevelopmentSeed:Subject in user secrets to your provider's sub claim to get sample notes.")]
    private static partial void LogSeedSkipped(ILogger logger);
}
