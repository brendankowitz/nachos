using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Api.Json;
using Nachos.Api.Paging;

namespace Nachos.Api.Endpoints;

internal static class PeerEndpoints
{
    public static void MapPeerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v3/workspaces/{workspace_id}/peers");
        endpoints.MapPost("/v3/workspaces/{workspace_id}/peers", async (string workspace_id, HttpContext http, INachosClient client) =>
        {
            using var body = await RequestBody.ReadAsync(http.Request);
            return TypedResults.Ok(await client.GetOrCreatePeerAsync(workspace_id, body.RequiredId(),
                body.Object("metadata"), body.Object("configuration"), http.RequestAborted));
        }).Accepts<PeerCreate>("application/json").Produces<Peer>();
        group.MapPost("/list", async (string workspace_id, HttpContext http, INachosClient client) =>
        {
            using var body = await RequestBody.ReadAsync(http.Request);
            var kind = body.Optional("kind", NachosJsonContext.Default.String) switch
            {
                null => (PeerKind?)null,
                "scope" => PeerKind.Scope,
                "all" => PeerKind.All,
                _ => throw RequestBody.Invalid(["body", "kind"], "Kind must be scope or all.", "literal_error"),
            };
            return TypedResults.Ok(await client.ListPeersAsync(workspace_id, kind, body.Object("filters"),
                PagingParameters.Read(http.Request), http.RequestAborted));
        }).Accepts<PeerGet>("application/json").Produces<Page<Peer>>().WithPaging();
        group.MapPut("/{peer_id}", async (string workspace_id, string peer_id, HttpContext http, INachosClient client) =>
        {
            using var body = await RequestBody.ReadAsync(http.Request);
            return TypedResults.Ok(await client.UpdatePeerAsync(workspace_id, peer_id, body.Object("metadata"),
                body.Object("configuration"), http.RequestAborted));
        }).Accepts<PeerUpdate>("application/json").Produces<Peer>();
        group.MapPost("/{peer_id}/sessions", async (string workspace_id, string peer_id, HttpContext http, INachosClient client) =>
        {
            using var body = await RequestBody.ReadAsync(http.Request);
            return TypedResults.Ok(await client.ListPeerSessionsAsync(workspace_id, peer_id, body.Object("filters"),
                PagingParameters.Read(http.Request), http.RequestAborted));
        }).Accepts<ResourceGet>("application/json").Produces<Page<Session>>().WithPaging();
    }
}
