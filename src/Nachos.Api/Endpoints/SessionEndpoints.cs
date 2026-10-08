using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Api.Json;
using Nachos.Api.Paging;

namespace Nachos.Api.Endpoints;

internal static class SessionEndpoints
{
    public static void MapSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v3/workspaces/{workspace_id}/sessions");
        endpoints.MapPost("/v3/workspaces/{workspace_id}/sessions", async Task<IResult> (string workspace_id, HttpContext http, INachosClient client) =>
        {
            using var body = await RequestBody.ReadAsync(http.Request);
            var scopes = body.OptionalStrings("scopes");
            if (scopes is { Length: > 100 })
            {
                throw RequestBody.Invalid(["body", "scopes"], "At most 100 scopes are allowed.", "too_long");
            }
            if (scopes is { Length: > 0 })
            {
                return NotImplementedEndpoints.Response();
            }
            return TypedResults.Ok(await client.GetOrCreateSessionAsync(workspace_id, body.RequiredId(),
                body.Object("metadata"), body.Optional("configuration", NachosJsonContext.Default.SessionConfiguration, strict: true),
                body.Peers("peers"), http.RequestAborted));
        }).Accepts<SessionCreate>("application/json").Produces<Session>();
        group.MapPost("/list", async (string workspace_id, HttpContext http, INachosClient client) =>
        {
            using var body = await RequestBody.ReadAsync(http.Request);
            return TypedResults.Ok(await client.ListSessionsAsync(workspace_id, body.Object("filters"),
                PagingParameters.Read(http.Request), http.RequestAborted));
        }).Accepts<ResourceGet>("application/json").Produces<Page<Session>>().WithPaging();
        group.MapPut("/{session_id}", async (string workspace_id, string session_id, HttpContext http, INachosClient client) =>
        {
            using var body = await RequestBody.ReadAsync(http.Request);
            return TypedResults.Ok(await client.UpdateSessionAsync(workspace_id, session_id, body.Object("metadata"),
                body.Optional("configuration", NachosJsonContext.Default.SessionConfiguration, strict: true), http.RequestAborted));
        }).Accepts<SessionUpdate>("application/json").Produces<Session>();
        group.MapPost("/{session_id}/peers", async (string workspace_id, string session_id, HttpContext http, INachosClient client) =>
        {
            using var body = await RequestBody.ReadAsync(http.Request);
            return TypedResults.Ok(await client.AddSessionPeersAsync(workspace_id, session_id, body.Peers()!, http.RequestAborted));
        }).Accepts<Dictionary<string, SessionPeerConfig>>("application/json").Produces<Session>();
        group.MapPut("/{session_id}/peers", async (string workspace_id, string session_id, HttpContext http, INachosClient client) =>
        {
            using var body = await RequestBody.ReadAsync(http.Request);
            return TypedResults.Ok(await client.SetSessionPeersAsync(workspace_id, session_id, body.Peers()!, http.RequestAborted));
        }).Accepts<Dictionary<string, SessionPeerConfig>>("application/json").Produces<Session>();
        group.MapDelete("/{session_id}/peers", async (string workspace_id, string session_id, HttpContext http, INachosClient client) =>
        {
            using var body = await RequestBody.ReadAsync(http.Request, requireObject: false);
            return TypedResults.Ok(await client.RemoveSessionPeersAsync(workspace_id, session_id,
                body.Strings(), http.RequestAborted));
        }).Accepts<string[]>("application/json").Produces<Session>();
        group.MapGet("/{session_id}/peers", async (string workspace_id, string session_id, HttpContext http, INachosClient client) =>
            TypedResults.Ok(await client.ListSessionPeersAsync(workspace_id, session_id,
                PagingParameters.Read(http.Request, allowReverse: false), http.RequestAborted)))
            .Produces<Page<Peer>>().WithPaging(allowReverse: false);
        group.MapGet("/{session_id}/peers/{peer_id}/config",
            async (string workspace_id, string session_id, string peer_id, HttpContext http, INachosClient client) =>
                TypedResults.Ok(await client.GetSessionPeerConfigAsync(workspace_id, session_id, peer_id, http.RequestAborted)))
            .Produces<SessionPeerConfig>();
        group.MapPut("/{session_id}/peers/{peer_id}/config",
            async (string workspace_id, string session_id, string peer_id, HttpContext http, INachosClient client) =>
            {
                using var body = await RequestBody.ReadAsync(http.Request);
                await client.SetSessionPeerConfigAsync(workspace_id, session_id, peer_id,
                    body.As(NachosJsonContext.Default.SessionPeerConfig), http.RequestAborted);
                return TypedResults.NoContent();
            }).Accepts<SessionPeerConfig>("application/json").Produces(204);
    }
}
