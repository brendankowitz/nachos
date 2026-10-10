using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.OpenApi;
using Nachos.Abstractions.Contracts;

namespace Nachos.Api.Json;

internal static class NachosOpenApi
{
    public static Task DescribeSchemasAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var type = context.JsonTypeInfo.Type;
        var property = context.JsonPropertyInfo;
        var declaringType = property?.DeclaringType;
        if (declaringType == typeof(SummaryConfiguration))
        {
            schema.Minimum = property!.Name switch
            {
                "messages_per_short_summary" => "10",
                "messages_per_long_summary" => "20",
                _ => schema.Minimum,
            };
        }
        else if (declaringType == typeof(MessageCreate) && property!.Name == "content")
        {
            schema.MinLength = 0;
            schema.MaxLength = 25_000;
        }
        else if (declaringType is { IsGenericType: true } && declaringType.GetGenericTypeDefinition() == typeof(Page<>))
        {
            schema.Minimum = property!.Name switch
            {
                "page" or "size" => "1",
                "total" or "pages" => "0",
                _ => schema.Minimum,
            };
        }
        if (type == typeof(Workspace) || type == typeof(Peer) || type == typeof(Session) || type == typeof(Message))
        {
            schema.Required?.Remove("metadata");
            schema.Required?.Remove("configuration");
        }
        return Task.CompletedTask;
    }

    public static async Task DescribeErrorsAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var relativePath = context.Description.RelativePath
            ?? throw new InvalidOperationException("An OpenAPI operation must have a route template.");
        var sharedParameters = context.Document?.Paths?.GetValueOrDefault("/" + relativePath.TrimStart('/'))?.Parameters;
        foreach (var parameter in RoutePatternFactory.Parse(relativePath).Parameters)
        {
            if ((operation.Parameters ?? []).Concat(sharedParameters ?? [])
                .Any(existing => existing.In == ParameterLocation.Path && existing.Name == parameter.Name))
            {
                continue;
            }
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = parameter.Name,
                In = ParameterLocation.Path,
                Required = true,
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            });
        }
        operation.Responses ??= new OpenApiResponses();
        if (operation.Responses.Count == 1 && operation.Responses.ContainsKey("501"))
        {
            return;
        }
        var validation = await context.GetOrCreateSchemaAsync(typeof(ValidationErrorResponse), null, cancellationToken);
        var domain = await context.GetOrCreateSchemaAsync(typeof(ErrorResponse), null, cancellationToken);
        operation.Responses["422"] = new OpenApiResponse
        {
            Description = "Request schema or domain validation failed.",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new()
                {
                    Schema = new OpenApiSchema { OneOf = [validation, domain] },
                },
            },
        };
    }
}
