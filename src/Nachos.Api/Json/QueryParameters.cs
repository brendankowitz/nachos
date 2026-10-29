using System.Globalization;
using Microsoft.OpenApi;

namespace Nachos.Api.Json;

/// <summary>
/// Typed query-string values. An absent parameter is null; a repeated one uses its last value; a value that does not
/// parse is a 422 located at <c>["query", name]</c>.
/// </summary>
internal static class QueryParameters
{
    public static string? Text(HttpRequest request, string name) =>
        request.Query.TryGetValue(name, out var values) ? values[^1] : null;

    public static int? Integer(HttpRequest request, string name)
    {
        var text = Text(request, name);
        if (text is null)
        {
            return null;
        }
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value : throw RequestBody.Invalid(["query", name], "Input should be a valid integer.", "int_parsing");
    }

    public static double? Number(HttpRequest request, string name)
    {
        var text = Text(request, name);
        if (text is null)
        {
            return null;
        }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value : throw RequestBody.Invalid(["query", name], "Input should be a valid number.", "float_parsing");
    }

    public static bool? Boolean(HttpRequest request, string name) => Text(request, name)?.ToLowerInvariant() switch
    {
        null => null,
        "true" or "1" or "yes" or "on" or "t" or "y" => true,
        "false" or "0" or "no" or "off" or "f" or "n" => false,
        _ => throw RequestBody.Invalid(["query", name], "Input should be a valid boolean.", "bool_parsing"),
    };

    /// <summary>Declares optional query parameters on the operation, in the order given.</summary>
    public static RouteHandlerBuilder WithQuery(this RouteHandlerBuilder endpoint, params QueryParameter[] parameters) =>
        endpoint.AddOpenApiOperationTransformer((operation, _, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            operation.Parameters ??= [];
            foreach (var parameter in parameters)
            {
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = parameter.Name, In = ParameterLocation.Query,
                    Schema = new OpenApiSchema
                    {
                        Type = parameter.Type | JsonSchemaType.Null, Minimum = parameter.Minimum, Maximum = parameter.Maximum,
                    },
                });
            }
            return Task.CompletedTask;
        });
}

/// <summary>One optional query parameter as OpenAPI documents it; the bounds are inclusive.</summary>
internal sealed record QueryParameter(string Name, JsonSchemaType Type, string? Minimum = null, string? Maximum = null);
