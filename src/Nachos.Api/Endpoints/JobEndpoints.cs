using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Api.Paging;

namespace Nachos.Api.Endpoints;

/// <summary><c>GET W/jobs</c>, a Nachos extension: the workspace's background jobs (in M3, its deletions).</summary>
internal static class JobEndpoints
{
    public static void MapJobEndpoints(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/v3/workspaces/{workspace_id}/jobs", async (string workspace_id, HttpContext http, INachosClient client) =>
            TypedResults.Ok(await client.ListJobsAsync(workspace_id, PagingParameters.Read(http.Request), http.RequestAborted)))
            .Produces<Page<Job>>().WithPaging()
            .WithDescription("Nachos extension. Oldest first; a session's deletion job stays listed after the session is removed.");
}
