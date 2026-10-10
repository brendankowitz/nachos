using Microsoft.OpenApi;
using Nachos.Api.Json;

namespace Nachos.Api.Idempotency;

internal static class IdempotencyEndpointAdapter
{
    public static string? Read(HttpRequest request)
    {
        var values = request.Headers["Idempotency-Key"];
        return values.Count switch
        {
            0 => null,
            1 => values[0],
            _ => throw RequestBody.Invalid(["header", "idempotency-key"],
                "Supply only one Idempotency-Key header value.", "value_error"),
        };
    }

    public static RouteHandlerBuilder WithIdempotencyKey(this RouteHandlerBuilder endpoint) =>
        endpoint.AddOpenApiOperationTransformer((operation, _, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "Idempotency-Key",
                In = ParameterLocation.Header,
                Required = false,
                Description = "Optional 1-255 ASCII-character key, passed unchanged; a comma is literal, not a separator. " +
                    "Multiple supplied values return 422 after authorization. " +
                    "For 24 hours, the same key and original body/target replay the captured response; " +
                    "a different body or session returns 422. Replays require current authorization.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 255 },
            });
            return Task.CompletedTask;
        });
}
