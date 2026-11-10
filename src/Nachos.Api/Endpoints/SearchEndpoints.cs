using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Api.Json;

namespace Nachos.Api.Endpoints;

/// <summary>The message search routes: <c>W/search</c>, <c>S/search</c> and <c>P/search</c>.</summary>
internal static class SearchEndpoints
{
    private const string W = "/v3/workspaces/{workspace_id}";

    public static void MapSearchEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(W + "/search", async (string workspace_id, HttpContext http, INachosClient client) =>
        {
            using var body = await RequestBody.ReadAsync(http.Request);
            body.Require("query");
            var found = await client.SearchWorkspaceAsync(workspace_id,
                body.As(NachosJsonContext.Default.WorkspaceMessageSearchOptions), http.RequestAborted);
            return TypedResults.Ok(found.ToArray());
        }).Accepts<WorkspaceMessageSearchOptions>("application/json").Produces<Message[]>()
            .WithDescription("Hybrid (vector and full-text) message search. Any scope value is a 422 until scopes land (M6).");
        endpoints.MapPost(W + "/sessions/{session_id}/search",
            async (string workspace_id, string session_id, HttpContext http, INachosClient client) =>
            {
                using var body = await RequestBody.ReadAsync(http.Request);
                body.Require("query");
                var found = await client.SearchSessionAsync(workspace_id, session_id,
                    body.As(NachosJsonContext.Default.MessageSearchOptions), http.RequestAborted);
                return TypedResults.Ok(found.ToArray());
            }).Accepts<MessageSearchOptions>("application/json").Produces<Message[]>();
        endpoints.MapPost(W + "/peers/{peer_id}/search",
            async (string workspace_id, string peer_id, HttpContext http, INachosClient client) =>
            {
                using var body = await RequestBody.ReadAsync(http.Request);
                body.Require("query");
                var found = await client.SearchPeerAsync(workspace_id, peer_id,
                    body.As(NachosJsonContext.Default.MessageSearchOptions), http.RequestAborted);
                return TypedResults.Ok(found.ToArray());
            }).Accepts<MessageSearchOptions>("application/json").Produces<Message[]>()
            .WithDescription("Searches the messages the peer could have seen: those of its sessions sent while it was a member.");
    }
}
