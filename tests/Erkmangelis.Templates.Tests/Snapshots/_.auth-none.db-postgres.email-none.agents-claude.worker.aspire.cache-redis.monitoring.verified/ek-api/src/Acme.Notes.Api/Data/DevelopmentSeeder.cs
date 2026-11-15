using Microsoft.EntityFrameworkCore;

namespace Acme.Notes.Api.Data;

/// <summary>Sample data for local development. Never runs outside the Development environment.</summary>
internal sealed class DevelopmentSeeder(AppDbContext db, TimeProvider time)
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (await db.Notes.IgnoreQueryFilters([SoftDelete.Filter]).AnyAsync(cancellationToken))
        {
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
                Title = titles[i],
                Content = "Development seed data.",
                CreatedAt = createdAt,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
