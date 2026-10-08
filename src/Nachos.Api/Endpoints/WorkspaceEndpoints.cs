using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Api.Json;
using Nachos.Api.Paging;

namespace Nachos.Api.Endpoints;

internal static class WorkspaceEndpoints
{
    public static void MapWorkspaceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/v3/workspaces");
        endpoints.MapPost("/v3/workspaces", async (HttpContext http, INachosClient client) =>
        {
            using var body = await RequestBody.ReadAsync(http.Request);
            var id = body.RequiredId();
            if (body.Root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == System.Text.Json.JsonValueKind.Null)
            {
                throw RequestBody.Invalid(["body", "metadata"], "Metadata must be an object.", "dict_type");
            }
            if (body.Root.TryGetProperty("configuration", out var configuration) && configuration.ValueKind == System.Text.Json.JsonValueKind.Null)
            {
                throw RequestBody.Invalid(["body", "configuration"], "Configuration must be an object.", "model_type");
            }
            return TypedResults.Ok(await client.GetOrCreateWorkspaceAsync(id, body.Object("metadata"),
                body.Optional("configuration", NachosJsonContext.Default.WorkspaceConfiguration, strict: true), http.RequestAborted));
        }).Accepts<WorkspaceCreate>("application/json").Produces<Workspace>();
        group.MapPost("/list", async (HttpContext http, INachosClient client) =>
        {
            using var body = await RequestBody.ReadAsync(http.Request);
            return TypedResults.Ok(await client.ListWorkspacesAsync(body.Object("filters"),
                PagingParameters.Read(http.Request), http.RequestAborted));
        }).Accepts<ResourceGet>("application/json").Produces<Page<Workspace>>().WithPaging();
        group.MapPut("/{workspace_id}", async (string workspace_id, HttpContext http, INachosClient client) =>
        {
            using var body = await RequestBody.ReadAsync(http.Request);
            return TypedResults.Ok(await client.UpdateWorkspaceAsync(workspace_id, body.Object("metadata"),
                body.Optional("configuration", NachosJsonContext.Default.WorkspaceConfiguration, strict: true), http.RequestAborted));
        }).Accepts<WorkspaceUpdate>("application/json").Produces<Workspace>();
    }
}
