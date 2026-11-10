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
            TypedResults.Ok(await client.GetSessionContextAsync(workspace_id, session_id, ReadQuery(http.Request), http.RequestAborted)))
            .Produces<SessionContext>().WithQuery(
            new("tokens", JsonSchemaType.Integer, "1", "100000"), new("summary", JsonSchemaType.Boolean),
            new("search_query", JsonSchemaType.String), new("peer_target", JsonSchemaType.String),
            new("peer_perspective", JsonSchemaType.String), new("scope", JsonSchemaType.String),
            new("sessions", JsonSchemaType.Array, Items: JsonSchemaType.String, MaxItems: MaxSessions), new("limit_to_session", JsonSchemaType.Boolean),
            new("search_top_k", JsonSchemaType.Integer, "1", "100"), new("search_max_distance", JsonSchemaType.Number, "0", "1"),
            new("include_most_frequent", JsonSchemaType.Boolean), new("max_conclusions", JsonSchemaType.Integer, "1", "100"))
            .WithDescription("The context never exceeds tokens content tokens (1 to 100,000; 100,000 when absent). Any scope " +
                $"value is a 422 until scopes land (M6). sessions names at most {MaxSessions} sessions. Under a peer-scoped " +
                "key, the key's peer must be an active member of this session and of every session named in sessions, and " +
                "peer_perspective (else peer_target) must be that peer. A session-scoped key may name only its own session " +
                "in sessions, and may pass peer_target or peer_perspective only with limit_to_session=true or sessions " +
                "naming exactly its own session. A scope needs a workspace or admin key. Otherwise the answer is 401.");
        group.MapGet("/summaries", async (string workspace_id, string session_id, HttpContext http, INachosClient client) =>
            TypedResults.Ok(await client.GetSessionSummariesAsync(workspace_id, session_id, http.RequestAborted)))
            .Produces<SessionSummaries>();
    }

    /// <summary>The most sessions one request may name.</summary>
    public const int MaxSessions = 100;

    /// <summary>
    /// The <c>S/context</c> query as the route binds it. Authorization reads it through this same method, so the
    /// parameters it checks are exactly the ones the route acts on.
    /// </summary>
    /// <exception cref="RequestValidationException">A value does not parse, or <c>sessions</c> names more than <see cref="MaxSessions"/>.</exception>
    public static SessionContextQuery ReadQuery(HttpRequest request)
    {
        var sessions = QueryParameters.Texts(request, "sessions");
        if (sessions is { Length: > MaxSessions })
        {
            throw RequestBody.Invalid(["query", "sessions"], $"At most {MaxSessions} sessions are allowed.", "too_long");
        }
        return new SessionContextQuery(
            QueryParameters.Integer(request, "tokens"),
            QueryParameters.Boolean(request, "summary"),
            QueryParameters.Text(request, "search_query"),
            QueryParameters.Text(request, "peer_target"),
            QueryParameters.Text(request, "peer_perspective"),
            QueryParameters.Text(request, "scope"),
            sessions,
            QueryParameters.Boolean(request, "limit_to_session"),
            QueryParameters.Integer(request, "search_top_k"),
            QueryParameters.Number(request, "search_max_distance"),
            QueryParameters.Boolean(request, "include_most_frequent"),
            QueryParameters.Integer(request, "max_conclusions"));
    }
}
