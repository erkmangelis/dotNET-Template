using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Acme.Notes.Api.Features.ApiKeys;

public sealed record CreateApiKeyRequest : IValidatableObject
{
    [Required]
    [StringLength(ApiKey.NameMaxLength, MinimumLength = 1)]
    public string Name { get; init; } = string.Empty;

    [Required]
    [MinLength(1)]
    [MaxLength(10)]
    public string[] Scopes { get; init; } = [];

    [Range(1, 365)]
    public int ExpiresInDays { get; init; } = 90;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        Scopes.Except(ApiKeyScopes.All, StringComparer.Ordinal).Any()
            ? [new ValidationResult($"Scopes must be among: {string.Join(", ", ApiKeyScopes.All)}.", [nameof(Scopes)])]
            : [];
}

public sealed record RotateApiKeyRequest
{
    /// <summary>How long the old key keeps working, so clients can switch over. 0 retires it at once.</summary>
    [Range(0, 168)]
    public int OverlapHours { get; init; }
}

public sealed record ApiKeyResponse(Guid Id, string Name, string Hint, IReadOnlyList<string> Scopes, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt, DateTimeOffset? LastUsedAt)
{
    internal static ApiKeyResponse From(ApiKey key) =>
        new(key.Id, key.Name, ApiKeyFormat.Hint(key.LookupId), key.Scopes, key.CreatedAt, key.ExpiresAt, key.RevokedAt, key.LastUsedAt);
}

/// <summary>The new key, shown this once. Store it in a secret manager; it cannot be shown again.</summary>
public sealed record CreatedApiKeyResponse(string Key, ApiKeyResponse ApiKey);

internal static class ApiKeyEndpoints
{
    public static IServiceCollection AddApiKeys(this IServiceCollection services)
    {
        services.AddScoped<ApiKeyService>();
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, configureOptions: null);
        return services;
    }

    /// <summary>
    /// Key management for the signed-in user. These endpoints use the normal user sign-in only, so an API
    /// key can never create, rotate or list keys.
    /// </summary>
    public static RouteGroupBuilder MapApiKeyEndpoints(this IEndpointRouteBuilder routes)
    {
        var keys = routes.MapGroup("/api-keys").WithTags("API keys").RequireAuthorization();

        keys.MapPost("/", CreateAsync).WithSummary("Create an API key; the key is in this response only");
        keys.MapGet("/", ListAsync).WithSummary("List your API keys");
        keys.MapPost("/{id:guid}/rotate", RotateAsync).WithSummary("Replace a key with a new one");
        keys.MapDelete("/{id:guid}", RevokeAsync).WithSummary("Revoke a key");

        return keys;
    }

    private static async Task<Results<Created<CreatedApiKeyResponse>, ProblemHttpResult>> CreateAsync(
        CreateApiKeyRequest request,
        ClaimsPrincipal user,
        ApiKeyService keys,
        CancellationToken cancellationToken)
    {
        var created = await keys.CreateAsync(user.GetUserId(), request.Name.Trim(), request.Scopes, TimeSpan.FromDays(request.ExpiresInDays), cancellationToken);
        if (created is not var (key, secret))
        {
            return TooManyKeys();
        }

        return TypedResults.Created($"/v1/api-keys/{key.Id}", new CreatedApiKeyResponse(secret, ApiKeyResponse.From(key)));
    }

    private static async Task<Ok<ApiKeyResponse[]>> ListAsync(ClaimsPrincipal user, ApiKeyService keys, CancellationToken cancellationToken) =>
        TypedResults.Ok((await keys.ListAsync(user.GetUserId(), cancellationToken)).Select(ApiKeyResponse.From).ToArray());

    private static async Task<Results<Created<CreatedApiKeyResponse>, NotFound>> RotateAsync(
        Guid id,
        RotateApiKeyRequest request,
        ClaimsPrincipal user,
        ApiKeyService keys,
        CancellationToken cancellationToken)
    {
        var rotated = await keys.RotateAsync(user.GetUserId(), id, TimeSpan.FromHours(request.OverlapHours), cancellationToken);
        if (rotated is not var (key, secret))
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Created($"/v1/api-keys/{key.Id}", new CreatedApiKeyResponse(secret, ApiKeyResponse.From(key)));
    }

    private static async Task<Results<NoContent, NotFound>> RevokeAsync(Guid id, ClaimsPrincipal user, ApiKeyService keys, CancellationToken cancellationToken) =>
        await keys.RevokeAsync(user.GetUserId(), id, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound();

    private static ProblemHttpResult TooManyKeys() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Too many API keys",
            detail: $"You can hold {ApiKeyService.MaxActiveKeys} active keys. Revoke one first.");
}
