using System.Globalization;
using Microsoft.OpenApi;

namespace Nachos.Api.Json;

/// <summary>
/// Typed query-string values. An absent parameter is null; a repeated one uses its last value (except
/// <see cref="Texts"/>, which keeps them all); a value that does not parse is a 422 located at <c>["query", name]</c>.
/// </summary>
internal static class QueryParameters
{
    public static string? Text(HttpRequest request, string name) =>
        request.Query.TryGetValue(name, out var values) ? values[^1] : null;

    /// <summary>Every value of a repeated parameter, in order; null when it is absent.</summary>
    public static string[]? Texts(HttpRequest request, string name) =>
        request.Query.TryGetValue(name, out var values) ? [.. values.Select(value => value ?? "")] : null;

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
                        Items = parameter.Items is { } items ? new OpenApiSchema { Type = items } : null,
                        MaxItems = parameter.MaxItems,
                    },
                });
            }
            return Task.CompletedTask;
        });
}

/// <summary>
/// One optional query parameter as OpenAPI documents it; the bounds are inclusive. <paramref name="Items"/> is the item
/// type of a repeated (array) parameter, and <paramref name="MaxItems"/> its most values.
/// </summary>
internal sealed record QueryParameter(string Name, JsonSchemaType Type, string? Minimum = null, string? Maximum = null,
    JsonSchemaType? Items = null, int? MaxItems = null);
