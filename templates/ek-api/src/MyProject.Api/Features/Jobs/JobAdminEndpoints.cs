using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MyProject.Api.Features.Jobs;

/// <summary>Failed background jobs, for roles with <see cref="Permissions.JobsManage"/>.</summary>
internal static class JobAdminEndpoints
{
    public static RouteGroupBuilder MapJobAdminEndpoints(this IEndpointRouteBuilder admin)
    {
        var jobs = admin.MapGroup("/jobs").RequirePermission(Permissions.JobsManage);
        jobs.MapGet("/failed", ListFailedAsync)
            .WithName("ListFailedJobs")
            .WithSummary("List jobs that used up their attempts, newest first");
        jobs.MapPost("/failed/{id}/requeue", RequeueAsync)
            .WithName("RequeueFailedJob")
            .WithSummary("Run a failed job again, with all its attempts");

        return jobs;
    }

    private static async Task<Ok<IReadOnlyList<FailedJob>>> ListFailedAsync(
        IFailedJobs failed,
        CancellationToken cancellationToken,
        [Range(1, 100)] int count = 50) =>
        TypedResults.Ok(await failed.ListAsync(count, cancellationToken));

    private static async Task<Results<Accepted, NotFound>> RequeueAsync(string id, IFailedJobs failed, CancellationToken cancellationToken) =>
        await failed.RequeueAsync(id, cancellationToken) ? TypedResults.Accepted((string?)null) : TypedResults.NotFound();
}
