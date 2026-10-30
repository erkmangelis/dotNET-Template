namespace MyProject.Api.Features.ApiKeys;

/// <summary>
/// A machine credential that acts for its owner with a limited set of scopes. Only a hash of the
/// secret is stored; the full key is shown once, when it is created.
/// </summary>
internal sealed class ApiKey
{
    public const int NameMaxLength = 100;

    public Guid Id { get; init; }

    public required string OwnerId { get; init; }

    public required string Name { get; init; }

    /// <summary>The public part of the key, used to find it. Not secret.</summary>
    public required string LookupId { get; init; }

    /// <summary>SHA-256 of the secret part.</summary>
    public required byte[] SecretHash { get; init; }

    public required string[] Scopes { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}

/// <summary>What a key may do. Users pick scopes when they create a key; endpoints opt in to keys with one.</summary>
internal static class ApiKeyScopes
{
    public const string ClaimType = "scope";

    public const string NotesRead = "notes:read";
    public const string NotesWrite = "notes:write";
#if (UseStorage)
    public const string FilesRead = "files:read";
    public const string FilesWrite = "files:write";

    public static readonly string[] All = [NotesRead, NotesWrite, FilesRead, FilesWrite];
#else

    public static readonly string[] All = [NotesRead, NotesWrite];
#endif
}
