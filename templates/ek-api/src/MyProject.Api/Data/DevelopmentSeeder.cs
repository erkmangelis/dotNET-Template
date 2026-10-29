#if (UseIdentity)
using Microsoft.AspNetCore.Identity;
#endif
using Microsoft.EntityFrameworkCore;

namespace MyProject.Api.Data;

/// <summary>Sample data for local development. Never runs outside the Development environment.</summary>
#if (UseIdentity)
internal sealed partial class DevelopmentSeeder(
    AppDbContext db,
    UserManager<AppUser> users,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<DevelopmentSeeder> logger)
#elif (UseOidc)
internal sealed partial class DevelopmentSeeder(
    AppDbContext db,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<DevelopmentSeeder> logger)
#else
internal sealed class DevelopmentSeeder(AppDbContext db, TimeProvider time)
#endif
{
#if (UseIdentity)
    public const string UserEmail = "dev@example.com";

#endif
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (await db.Notes.AnyAsync(cancellationToken))
        {
            return;
        }

#if (UseIdentity)
        // The password comes from user secrets: dotnet user-secrets set DevelopmentSeed:UserPassword "<password>"
        var password = configuration["DevelopmentSeed:UserPassword"];
        if (string.IsNullOrEmpty(password))
        {
            LogSeedSkipped(logger);
            return;
        }

        var user = await users.FindByEmailAsync(UserEmail);
        if (user is null)
        {
            user = new AppUser { UserName = UserEmail, Email = UserEmail, EmailConfirmed = true };
            var created = await users.CreateAsync(user, password);
            if (!created.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Could not create the development user: {string.Join("; ", created.Errors.Select(error => error.Description))}");
            }

            await users.AddToRoleAsync(user, Roles.Admin);
        }

        var ownerId = user.Id;
#elif (UseOidc)
        // Accounts live in the provider, so the notes go to the subject named in configuration. The
        // default is the dev user in keycloak/realm.json; for another provider set your own sub:
        // dotnet user-secrets set DevelopmentSeed:Subject "<your sub claim>"
        var ownerId = configuration["DevelopmentSeed:Subject"];
        if (string.IsNullOrEmpty(ownerId))
        {
            LogSeedSkipped(logger);
            return;
        }

#endif
        var now = time.GetUtcNow();
        string[] titles = ["Welcome", "Try the API reference at /scalar", "Delete this seed data when you start"];
        for (var i = 0; i < titles.Length; i++)
        {
            var createdAt = now.AddMinutes(-i);
            db.Notes.Add(new Note
            {
                Id = Guid.CreateVersion7(createdAt),
#if (UseAuth)
                OwnerId = ownerId,
#endif
                Title = titles[i],
                Content = "Development seed data.",
                CreatedAt = createdAt,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }
#if (UseIdentity)

    [LoggerMessage(Level = LogLevel.Information, Message = "Development seed skipped: set DevelopmentSeed:UserPassword in user secrets to create " + UserEmail + " with sample notes.")]
    private static partial void LogSeedSkipped(ILogger logger);
#elif (UseOidc)

    [LoggerMessage(Level = LogLevel.Information, Message = "Development seed skipped: set DevelopmentSeed:Subject in user secrets to your provider's sub claim to get sample notes.")]
    private static partial void LogSeedSkipped(ILogger logger);
#endif
}
