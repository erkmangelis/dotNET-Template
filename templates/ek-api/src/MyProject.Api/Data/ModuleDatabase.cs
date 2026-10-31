using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Options;

namespace MyProject.Api.Data;

/// <summary>
/// A feature with tables of its own: its own context, migrations and migration history table, in the
/// app's database. Turning the feature on or off adds or removes its migrations without touching the
/// app's. Keep its entity configuration inside its context: <see cref="AppDbContext"/> picks up every
/// <see cref="IEntityTypeConfiguration{TEntity}"/> class in the assembly.
/// </summary>
/// <param name="ContextType">The feature's context, migrated after <see cref="AppDbContext"/> at startup.</param>
internal sealed record ModuleDatabase(Type ContextType)
{
    public static IServiceCollection Add<TContext>(IServiceCollection services, string historyTable)
        where TContext : DbContext
    {
        services.AddDbContext<TContext>((provider, options) => options
            .UseAppDatabase(provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.ConnectionString, historyTable));
        services.AddSingleton(new ModuleDatabase(typeof(TContext)));
        return services;
    }
}

/// <summary>Lets <c>dotnet ef</c> create a feature context without starting the app, like <see cref="AppDbContextFactory"/>.</summary>
internal abstract class ModuleDbContextFactory<TContext>(string historyTable, Func<DbContextOptions<TContext>, TContext> create)
    : IDesignTimeDbContextFactory<TContext>
    where TContext : DbContext
{
    public TContext CreateDbContext(string[] args) =>
        create(new DbContextOptionsBuilder<TContext>().UseAppDatabase(AppDbContextFactory.ConnectionString, historyTable).Options);
}

internal enum OutboxStatus
{
    /// <summary>Waiting for its next attempt at <see cref="IOutboxItem.NextAttemptAt"/>.</summary>
    Pending,

    Succeeded,

    /// <summary>Gave up after its last attempt, or nothing can handle it. It can be retried by hand.</summary>
    Failed,
}

/// <summary>
/// A row of work that a background loop sends or processes, with retries. Writing the row is the
/// promise; the loop keeps it, so the work survives restarts and happens at least once.
/// </summary>
internal interface IOutboxItem
{
    Guid Id { get; }

    OutboxStatus Status { get; set; }

    int Attempts { get; set; }

    DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>Set while an instance works on it, so another instance leaves it alone. A crashed instance's claim runs out.</summary>
    DateTimeOffset? LockedUntil { get; set; }

    string? LastError { get; set; }
}

internal static class Outbox
{
    public const int ErrorMaxLength = 500;

    /// <summary>
    /// Up to <paramref name="batchSize"/> due items, each claimed for this instance for <paramref name="lease"/>.
    /// The claim is a conditional update, so two instances never both get the same item.
    /// </summary>
    public static async Task<List<T>> ClaimDueAsync<T>(DbSet<T> items, DateTimeOffset now, TimeSpan lease, int batchSize, CancellationToken cancellationToken)
        where T : class, IOutboxItem
    {
        var due = await items
            .Where(item => item.Status == OutboxStatus.Pending && item.NextAttemptAt <= now && (item.LockedUntil == null || item.LockedUntil < now))
            .OrderBy(item => item.NextAttemptAt)
            .Select(item => item.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        var until = now + lease;
        var claimed = new List<Guid>();
        foreach (var id in due)
        {
            var updated = await items
                .Where(item => item.Id == id && item.Status == OutboxStatus.Pending && (item.LockedUntil == null || item.LockedUntil < now))
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.LockedUntil, until), cancellationToken);
            if (updated == 1)
            {
                claimed.Add(id);
            }
        }

        return claimed.Count == 0 ? [] : await items.Where(item => claimed.Contains(item.Id)).ToListAsync(cancellationToken);
    }

    public static void Succeeded(IOutboxItem item)
    {
        item.Attempts++;
        item.Status = OutboxStatus.Succeeded;
        item.LockedUntil = null;
        item.LastError = null;
    }

    /// <summary>Schedules the next attempt after <paramref name="retryDelays"/>[attempts so far], or gives up when none is left.</summary>
    public static void Failed(IOutboxItem item, string error, DateTimeOffset now, IReadOnlyList<int> retryDelays)
    {
        item.Attempts++;
        item.LockedUntil = null;
        item.LastError = error.Length > ErrorMaxLength ? error[..ErrorMaxLength] : error;
        if (item.Attempts > retryDelays.Count)
        {
            item.Status = OutboxStatus.Failed;
            return;
        }

        item.NextAttemptAt = now.AddSeconds(retryDelays[item.Attempts - 1]);
    }

    /// <summary>Gives up at once: a failure that retrying cannot fix.</summary>
    public static void GiveUp(IOutboxItem item, string error)
    {
        item.Attempts++;
        item.LockedUntil = null;
        item.LastError = error.Length > ErrorMaxLength ? error[..ErrorMaxLength] : error;
        item.Status = OutboxStatus.Failed;
    }

    /// <summary>Puts a failed item back in the queue with a fresh set of attempts.</summary>
    public static void Retry(IOutboxItem item, DateTimeOffset now)
    {
        item.Status = OutboxStatus.Pending;
        item.Attempts = 0;
        item.NextAttemptAt = now;
        item.LockedUntil = null;
    }
}
