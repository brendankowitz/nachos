using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Api.Json;
using Nachos.Api.Paging;

namespace Nachos.Api.Endpoints;

internal static class ConclusionEndpoints
{
    public static void MapConclusionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v3/workspaces/{workspace_id}/conclusions");
        // No Idempotency-Key in M2 (spec §9.1): a repeated create stores a second batch, as Honcho does.
        endpoints.MapPost("/v3/workspaces/{workspace_id}/conclusions", async (string workspace_id, HttpContext http, INachosClient client) =>
        {
            using var body = await RequestBody.ReadAsync(http.Request);
            body.Require("conclusions");
            var batch = body.As(NachosJsonContext.Default.ConclusionBatchCreate);
            var created = await client.CreateConclusionsAsync(workspace_id, batch.Conclusions, http.RequestAborted);
            return TypedResults.Json(created.ToArray(), NachosJsonContext.Default.ConclusionArray, statusCode: 201);
        }).Accepts<ConclusionBatchCreate>("application/json").Produces<Conclusion[]>(201);
        group.MapPost("/list", async (string workspace_id, HttpContext http, INachosClient client) =>
        {
            using var body = await RequestBody.ReadAsync(http.Request, allowEmpty: true);
            return TypedResults.Ok(await client.ListConclusionsAsync(workspace_id, body.Object("filters"),
                PagingParameters.Read(http.Request), http.RequestAborted));
        }).Accepts<ConclusionGet>("application/json").Produces<Page<Conclusion>>().WithPaging();
        group.MapPost("/query", async (string workspace_id, HttpContext http, INachosClient client) =>
        {
            using var body = await RequestBody.ReadAsync(http.Request);
            body.Require("query");
            var found = await client.QueryConclusionsAsync(workspace_id, body.As(NachosJsonContext.Default.ConclusionQuery),
                http.RequestAborted);
            return TypedResults.Ok(found.ToArray());
        }).Accepts<ConclusionQuery>("application/json").Produces<Conclusion[]>()
            .WithDescription("Semantic query. A host without an embedding generator answers 500 by design.");
        group.MapGet("/{conclusion_id}", async (string workspace_id, string conclusion_id, HttpContext http, INachosClient client) =>
            TypedResults.Ok(await client.GetConclusionAsync(workspace_id, conclusion_id, http.RequestAborted)))
            .Produces<Conclusion>();
        group.MapDelete("/{conclusion_id}", async (string workspace_id, string conclusion_id, HttpContext http, INachosClient client) =>
        {
            await client.DeleteConclusionAsync(workspace_id, conclusion_id, http.RequestAborted);
            return TypedResults.NoContent();
        }).Produces(204);
    }
}
