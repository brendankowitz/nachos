using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Nachos.Abstractions;
using Nachos.Api.Json;

namespace Nachos.Api.Paging;

internal static class PagingParameters
{
    public static PageRequest Read(HttpRequest request, bool allowReverse = true) =>
        new(QueryParameters.Integer(request, "page") ?? 1, QueryParameters.Integer(request, "size") ?? PageRequest.DefaultSize,
            allowReverse && (QueryParameters.Boolean(request, "reverse") ?? false));

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
            return Task.CompletedTask;
        });
}
