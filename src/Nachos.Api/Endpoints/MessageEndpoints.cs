using System.Text;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Api.Idempotency;
using Nachos.Api.Json;
using Nachos.Api.Paging;
using Nachos.Core;

namespace Nachos.Api.Endpoints;

internal static class MessageEndpoints
{
    public static void MapMessageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v3/workspaces/{workspace_id}/sessions/{session_id}/messages");
        // ASP.NET routing accepts the trailing slash as the same endpoint, so there is no ambiguous second mapping.
        endpoints.MapPost("/v3/workspaces/{workspace_id}/sessions/{session_id}/messages",
            async (string workspace_id, string session_id, HttpContext http, NachosService service) =>
        {
            using var body = await RequestBody.ReadAsync(http.Request, requireObject: false);
            var key = IdempotencyEndpointAdapter.Read(http.Request);
            var captured = await service.CreateMessagesResponseAsync(workspace_id, session_id, body.Root, key, http.RequestAborted);
            http.Response.StatusCode = captured.Status;
            http.Response.ContentType = "application/json; charset=utf-8";
            await http.Response.WriteAsync(captured.Body, Encoding.UTF8, http.RequestAborted);
        }).Accepts<MessageBatchCreate>("application/json").Produces<Message[]>(201).WithIdempotencyKey();
        group.MapPost("/list", async (string workspace_id, string session_id, HttpContext http, INachosClient client) =>
        {
            using var body = await RequestBody.ReadAsync(http.Request, allowEmpty: true);
            return TypedResults.Ok(await client.ListMessagesAsync(workspace_id, session_id, body.Object("filters"),
                PagingParameters.Read(http.Request), http.RequestAborted));
        }).Accepts<ResourceGet>("application/json").Produces<Page<Message>>().WithPaging();
        group.MapGet("/{message_id}", async (string workspace_id, string session_id, string message_id, HttpContext http, INachosClient client) =>
            TypedResults.Ok(await client.GetMessageAsync(workspace_id, session_id, message_id, http.RequestAborted)))
            .Produces<Message>();
        group.MapPut("/{message_id}", async (string workspace_id, string session_id, string message_id, HttpContext http, INachosClient client) =>
        {
            using var body = await RequestBody.ReadAsync(http.Request);
            return TypedResults.Ok(await client.UpdateMessageAsync(workspace_id, session_id, message_id,
                body.Object("metadata"), http.RequestAborted));
        }).Accepts<MessageUpdate>("application/json").Produces<Message>();
    }
}
