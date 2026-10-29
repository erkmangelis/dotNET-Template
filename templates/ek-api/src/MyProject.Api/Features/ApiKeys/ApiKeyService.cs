using Microsoft.EntityFrameworkCore;

namespace MyProject.Api.Features.ApiKeys;

/// <summary>Creates, lists, rotates and revokes a user's API keys. Every method is scoped to one owner.</summary>
internal sealed class ApiKeyService(AppDbContext db, TimeProvider time)
{
    /// <summary>Active keys a user may hold at once.</summary>
    public const int MaxActiveKeys = 20;

    public async Task<(ApiKey Key, string Secret)?> CreateAsync(string ownerId, string name, string[] scopes, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var active = await db.ApiKeys.CountAsync(key => key.OwnerId == ownerId && key.RevokedAt == null && key.ExpiresAt > now, cancellationToken);
        if (active >= MaxActiveKeys)
        {
            return null;
        }

        var (secret, lookupId, hash) = ApiKeyFormat.Generate();
        var key = new ApiKey
        {
            Id = Guid.CreateVersion7(now),
            OwnerId = ownerId,
            Name = name,
            LookupId = lookupId,
            SecretHash = hash,
            Scopes = [.. scopes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            CreatedAt = now,
            ExpiresAt = now + lifetime,
        };
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(cancellationToken);

        return (key, secret);
    }

    public Task<List<ApiKey>> ListAsync(string ownerId, CancellationToken cancellationToken) =>
        db.ApiKeys.AsNoTracking()
            .Where(key => key.OwnerId == ownerId)
            .OrderByDescending(key => key.CreatedAt)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Issues a new key with the same name, scopes and lifetime, and retires the old one after
    /// <paramref name="overlap"/> (immediately when it is zero), so clients can switch without downtime.
    /// </summary>
    public async Task<(ApiKey Key, string Secret)?> RotateAsync(string ownerId, Guid id, TimeSpan overlap, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var old = await db.ApiKeys.FirstOrDefaultAsync(key => key.Id == id && key.OwnerId == ownerId, cancellationToken);
        if (old is null || !old.IsActive(now))
        {
            return null;
        }

        var lifetime = old.ExpiresAt - old.CreatedAt;

        if (overlap <= TimeSpan.Zero)
        {
            old.RevokedAt = now;
        }
        else if (now + overlap < old.ExpiresAt)
        {
            old.ExpiresAt = now + overlap;
        }

        var (secret, lookupId, hash) = ApiKeyFormat.Generate();
        var key = new ApiKey
        {
            Id = Guid.CreateVersion7(now),
            OwnerId = ownerId,
            Name = old.Name,
            LookupId = lookupId,
            SecretHash = hash,
            Scopes = old.Scopes,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
        };
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(cancellationToken);

        return (key, secret);
    }

    public async Task<bool> RevokeAsync(string ownerId, Guid id, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var revoked = await db.ApiKeys
            .Where(key => key.Id == id && key.OwnerId == ownerId && key.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(key => key.RevokedAt, now), cancellationToken);
        return revoked == 1;
    }

    /// <summary>Revokes every key the user has, for example when the account changes hands.</summary>
    public Task RevokeAllAsync(string ownerId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        return db.ApiKeys
            .Where(key => key.OwnerId == ownerId && key.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(key => key.RevokedAt, now), cancellationToken);
    }
}
