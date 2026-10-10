using Nachos.Abstractions;
using Nachos.Api.Json;

namespace Nachos.Api.Endpoints;

internal static class GrantEndpoints
{
    internal static void MapGrantEndpoints(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/v3/admin/grants", async (HttpContext http, INachosClient client) =>
        {
            using var body = await RequestBody.ReadAsync(http.Request);
            var request = body.As(NachosJsonContext.Default.GrantCreate);
            await client.AddGrantAsync(request.ObjectId, request.WorkspaceId, request.Role, http.RequestAborted);
            return TypedResults.NoContent();
        }).Accepts<GrantCreate>("application/json").Produces(204);
}
