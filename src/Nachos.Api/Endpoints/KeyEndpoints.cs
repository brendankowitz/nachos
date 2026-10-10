using System.Globalization;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Api.Json;
using Nachos.Core.Keys;

namespace Nachos.Api.Endpoints;

internal static class KeyEndpoints
{
    private static readonly string[] QueryNames = ["workspace_id", "peer_id", "session_id", "expires_at"];

    internal static void MapKeyEndpoints(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/v3/keys", async (HttpContext http, INachosClient client, IConfiguration configuration,
            IOptions<SigningKeyOptions> signingKeys) =>
        {
            if (!configuration.GetValue("Nachos:Auth:Enabled", true) || signingKeys.Value.Keys.Count == 0)
                throw new NachosValidationException("key issuance requires configured signing keys");
            var query = http.Request.Query;
            var expiry = query["expires_at"].LastOrDefault();
            DateTimeOffset? expiresAt = null;
            if (expiry is not null)
            {
                if (!DateTimeOffset.TryParse(expiry, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var parsed))
                    throw RequestBody.Invalid(["query", "expires_at"], "Input should be a valid datetime.", "datetime_parsing");
                expiresAt = parsed;
            }
            return TypedResults.Ok(await client.CreateKeyAsync(query["workspace_id"].LastOrDefault(),
                query["peer_id"].LastOrDefault(), query["session_id"].LastOrDefault(), expiresAt, http.RequestAborted));
        }).Produces<KeyResponse>().AddOpenApiOperationTransformer((operation, _, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            operation.Parameters ??= [];
            foreach (var name in QueryNames)
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = name, In = ParameterLocation.Query,
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = name == "expires_at" ? "date-time" : null },
                });
            return Task.CompletedTask;
        });
}
