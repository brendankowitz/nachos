using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Nachos.Abstractions;
using Nachos.Api.Json;

namespace Nachos.Api.Paging;

internal static class PagingParameters
{
    private const string Cursor = "cursor";

    // Cursor pagination answers a different envelope (CursorPage, no total) that Nachos does not produce, so any cursor,
    // including the empty first-page opt-in, is refused rather than answered with an offset page.
    public static PageRequest Read(HttpRequest request, bool allowReverse = true)
    {
        if (request.Query.ContainsKey(Cursor))
        {
            throw QueryParameters.NotSupported(Cursor);
        }
        return new(QueryParameters.Integer(request, "page") ?? 1, QueryParameters.Integer(request, "size") ?? PageRequest.DefaultSize,
            allowReverse && (QueryParameters.Boolean(request, "reverse") ?? false));
    }

    public static RouteHandlerBuilder WithPaging(this RouteHandlerBuilder endpoint, bool allowReverse = true) =>
        endpoint.AddOpenApiOperationTransformer((operation, _, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "page", In = ParameterLocation.Query,
                Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Default = JsonValue.Create(1), Minimum = "1" },
            });
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "size", In = ParameterLocation.Query,
                Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.Integer, Default = JsonValue.Create(PageRequest.DefaultSize),
                    Minimum = "1", Maximum = "100",
                },
            });
            if (allowReverse)
            {
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "reverse", In = ParameterLocation.Query,
                    Schema = new OpenApiSchema { Type = JsonSchemaType.Boolean, Default = JsonValue.Create(false) },
                });
            }
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = Cursor, In = ParameterLocation.Query,
                Description = "Cursor pagination is not supported in this Nachos version; any value, including empty, is a 422.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null },
            });
            return Task.CompletedTask;
        });
}
