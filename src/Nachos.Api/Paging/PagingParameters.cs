using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Nachos.Abstractions;
using Nachos.Api.Json;

namespace Nachos.Api.Paging;

internal static class PagingParameters
{
    public static PageRequest Read(HttpRequest request, bool allowReverse = true) =>
        new(Integer(request, "page", 1), Integer(request, "size", PageRequest.DefaultSize),
            allowReverse && Boolean(request, "reverse"));

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

    private static int Integer(HttpRequest request, string field, int fallback)
    {
        if (!request.Query.TryGetValue(field, out var values))
        {
            return fallback;
        }
        if (int.TryParse(values[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }
        throw RequestBody.Invalid(["query", field], "Input should be a valid integer.", "int_parsing");
    }

    private static bool Boolean(HttpRequest request, string field)
    {
        if (!request.Query.TryGetValue(field, out var values))
        {
            return false;
        }
        return values[^1]?.ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" or "t" or "y" => true,
            "false" or "0" or "no" or "off" or "f" or "n" => false,
            _ => throw RequestBody.Invalid(["query", field], "Input should be a valid boolean.", "bool_parsing"),
        };
    }
}
