using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Acme.Notes.Api.Features.Audit;

/// <summary>The audit history of every record, under <c>/v1/admin/audit</c>, for roles with <see cref="Permissions.AuditRead"/>.</summary>
internal static class AuditAdminEndpoints
{
    public static RouteGroupBuilder MapAuditAdminEndpoints(this IEndpointRouteBuilder admin)
    {
        var audit = admin.MapGroup("/audit");
        audit.MapGet("/", ListAsync)
            .RequirePermission(Permissions.AuditRead)
            .WithName("ListAuditEntries")
            .WithSummary("List audit entries, newest first, by record or by actor");

        return audit;
    }

    private static async Task<Ok<PagedResult<AdminAuditEntryResponse>>> ListAsync(
        AuditLog log,
        CancellationToken cancellationToken,
        [StringLength(AuditEntry.EntityTypeMaxLength)] string? entityType = null,
        [StringLength(AuditEntry.EntityIdMaxLength)] string? entityId = null,
        [StringLength(450)] string? actorId = null,
        [Range(1, Paging.MaxPage)] int page = 1,
        [Range(1, Paging.MaxPageSize)] int pageSize = Paging.DefaultPageSize)
    {
        var entries = await log.ListAsync(entityType, entityId, actorId, page, pageSize, cancellationToken);
        return TypedResults.Ok(entries.Map(AdminAuditEntryResponse.From));
    }
}
