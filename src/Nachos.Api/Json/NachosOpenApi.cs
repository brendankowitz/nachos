using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Nachos.Abstractions.Contracts;

namespace Nachos.Api.Json;

internal static class NachosOpenApi
{
    public static async Task DescribeErrorsAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        operation.Responses ??= new OpenApiResponses();
        if (operation.Responses.ContainsKey("501"))
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
