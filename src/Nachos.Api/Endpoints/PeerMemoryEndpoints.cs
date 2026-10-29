using Microsoft.OpenApi;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Api.Json;

namespace Nachos.Api.Endpoints;

/// <summary>The peer card, working representation and peer context routes.</summary>
internal static class PeerMemoryEndpoints
{
    public static void MapPeerMemoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v3/workspaces/{workspace_id}/peers/{peer_id}");
        group.MapGet("/card", async (string workspace_id, string peer_id, HttpContext http, INachosClient client) =>
            TypedResults.Ok(await client.GetPeerCardAsync(workspace_id, peer_id, QueryParameters.Text(http.Request, "target"),
                http.RequestAborted)))
            .Produces<PeerCardResponse>().WithQuery(("target", JsonSchemaType.String));
        group.MapPut("/card", async (string workspace_id, string peer_id, HttpContext http, INachosClient client) =>
        {
            using var body = await RequestBody.ReadAsync(http.Request);
            body.Require("peer_card");
            // An explicit null reaches Core, which reports it as list_type.
            var card = body.OptionalStrings("peer_card");
            return TypedResults.Ok(await client.SetPeerCardAsync(workspace_id, peer_id, card!,
                QueryParameters.Text(http.Request, "target"), http.RequestAborted));
        }).Accepts<PeerCardSet>("application/json").Produces<PeerCardResponse>().WithQuery(("target", JsonSchemaType.String));
        group.MapPost("/representation", async (string workspace_id, string peer_id, HttpContext http, INachosClient client) =>
        {
            using var body = await RequestBody.ReadAsync(http.Request, allowEmpty: true);
            return TypedResults.Ok(await client.GetRepresentationAsync(workspace_id, peer_id,
                body.As(NachosJsonContext.Default.PeerRepresentationGet), http.RequestAborted));
        }).Accepts<PeerRepresentationGet>("application/json").Produces<RepresentationResponse>()
            .WithDescription("Any scope value is a 422 until scopes land (M6).");
        group.MapGet("/context", async (string workspace_id, string peer_id, HttpContext http, INachosClient client) =>
        {
            var request = http.Request;
            var query = new PeerContextQuery(
                QueryParameters.Text(request, "target"),
                QueryParameters.Text(request, "search_query"),
                QueryParameters.Integer(request, "search_top_k"),
                QueryParameters.Number(request, "search_max_distance"),
                QueryParameters.Boolean(request, "include_most_frequent"),
                QueryParameters.Integer(request, "max_conclusions"));
            return TypedResults.Ok(await client.GetPeerContextAsync(workspace_id, peer_id, query, http.RequestAborted));
        }).Produces<PeerContext>().WithQuery(
            ("target", JsonSchemaType.String), ("search_query", JsonSchemaType.String),
            ("search_top_k", JsonSchemaType.Integer), ("search_max_distance", JsonSchemaType.Number),
            ("include_most_frequent", JsonSchemaType.Boolean), ("max_conclusions", JsonSchemaType.Integer));
    }
}
