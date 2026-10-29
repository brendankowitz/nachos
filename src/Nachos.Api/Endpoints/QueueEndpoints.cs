using Microsoft.OpenApi;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Api.Json;

namespace Nachos.Api.Endpoints;

internal static class QueueEndpoints
{
    public static void MapQueueEndpoints(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/v3/workspaces/{workspace_id}/queue/status", async (string workspace_id, HttpContext http, INachosClient client) =>
        {
            var request = http.Request;
            return TypedResults.Ok(await client.GetQueueStatusAsync(workspace_id, QueryParameters.Text(request, "observer_id"),
                QueryParameters.Text(request, "sender_id"), QueryParameters.Text(request, "session_id"), http.RequestAborted));
        }).Produces<QueueStatus>().WithQuery(
            ("observer_id", JsonSchemaType.String), ("sender_id", JsonSchemaType.String), ("session_id", JsonSchemaType.String));
}
