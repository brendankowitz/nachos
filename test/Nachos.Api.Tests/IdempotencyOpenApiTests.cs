using System.Text.Json;
using Shouldly;

namespace Nachos.Api.Tests;

public sealed class IdempotencyOpenApiTests : ApiTest
{
    [Fact]
    public async Task OptionalLiteralKey_IsDocumentedOnlyOnMessageCreate()
    {
        var document = await Send(HttpMethod.Get, "/openapi/v1.json");
        var found = 0;
        foreach (var path in document.GetProperty("paths").EnumerateObject())
        foreach (var operation in path.Value.EnumerateObject())
        {
            if (operation.Value.ValueKind != JsonValueKind.Object ||
                !operation.Value.TryGetProperty("parameters", out var parameters)) continue;
            foreach (var parameter in parameters.EnumerateArray().Where(p =>
                p.GetProperty("name").GetString() == "Idempotency-Key"))
            {
                found++;
                path.Name.ShouldBe("/v3/workspaces/{workspace_id}/sessions/{session_id}/messages");
                operation.Name.ShouldBe("post");
                parameter.GetProperty("in").GetString().ShouldBe("header");
                (parameter.TryGetProperty("required", out var required) && required.GetBoolean()).ShouldBeFalse();
                var schema = parameter.GetProperty("schema");
                schema.GetProperty("type").GetString().ShouldBe("string");
                schema.GetProperty("minLength").GetInt32().ShouldBe(1);
                schema.GetProperty("maxLength").GetInt32().ShouldBe(255);
                var description = parameter.GetProperty("description").GetString()!;
                foreach (var word in new[] { "ASCII", "comma", "unchanged", "24", "authorization", "422" })
                    description.ShouldContain(word);
                operation.Value.GetProperty("responses").GetProperty("422").GetProperty("content")
                    .GetProperty("application/json").GetProperty("schema").GetProperty("oneOf").GetArrayLength().ShouldBe(2);
            }
        }
        found.ShouldBe(1);
    }
}
