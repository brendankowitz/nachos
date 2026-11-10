using Microsoft.OpenApi;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Api.Json;

namespace Nachos.Api.Endpoints;

/// <summary>The session context and summaries routes (<c>GET S/context</c>, <c>GET S/summaries</c>).</summary>
internal static class SessionContextEndpoints
{
    public static void MapSessionContextEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v3/workspaces/{workspace_id}/sessions/{session_id}");
        group.MapGet("/context", async (string workspace_id, string session_id, HttpContext http, INachosClient client) =>
        {
            var request = http.Request;
            var query = new SessionContextQuery(
                QueryParameters.Integer(request, "tokens"),
                QueryParameters.Boolean(request, "summary"),
                QueryParameters.Text(request, "search_query"),
                QueryParameters.Text(request, "peer_target"),
                QueryParameters.Text(request, "peer_perspective"),
                QueryParameters.Text(request, "scope"),
                QueryParameters.Texts(request, "sessions"),
                QueryParameters.Boolean(request, "limit_to_session"),
                QueryParameters.Integer(request, "search_top_k"),
                QueryParameters.Number(request, "search_max_distance"),
                QueryParameters.Boolean(request, "include_most_frequent"),
                QueryParameters.Integer(request, "max_conclusions"));
            return TypedResults.Ok(await client.GetSessionContextAsync(workspace_id, session_id, query, http.RequestAborted));
        }).Produces<SessionContext>().WithQuery(
            new("tokens", JsonSchemaType.Integer, "1", "100000"), new("summary", JsonSchemaType.Boolean),
            new("search_query", JsonSchemaType.String), new("peer_target", JsonSchemaType.String),
            new("peer_perspective", JsonSchemaType.String), new("scope", JsonSchemaType.String),
            new("sessions", JsonSchemaType.Array, Items: JsonSchemaType.String), new("limit_to_session", JsonSchemaType.Boolean),
            new("search_top_k", JsonSchemaType.Integer, "1", "100"), new("search_max_distance", JsonSchemaType.Number, "0", "1"),
            new("include_most_frequent", JsonSchemaType.Boolean), new("max_conclusions", JsonSchemaType.Integer, "1", "100"))
            .WithDescription("The context never exceeds tokens content tokens (1 to 100,000; 100,000 when absent). Any scope " +
                "value is a 422 until scopes land (M6). Under a peer-scoped key, the key's peer must be an active member of " +
                "this session and of every session named in sessions, and peer_perspective (else peer_target) must be that " +
                "peer. A session-scoped key may name only its own session in sessions, and a scope needs a workspace or " +
                "admin key. Otherwise the answer is 401.");
        group.MapGet("/summaries", async (string workspace_id, string session_id, HttpContext http, INachosClient client) =>
            TypedResults.Ok(await client.GetSessionSummariesAsync(workspace_id, session_id, http.RequestAborted)))
            .Produces<SessionSummaries>();
    }
}
