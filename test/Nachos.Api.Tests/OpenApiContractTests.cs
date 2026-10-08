using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nachos.Api.Json;
using Shouldly;

namespace Nachos.Api.Tests;

public sealed class OpenApiContractTests : ApiTest
{
    [Fact]
    public void HttpAndGeneratedSerializers_BothUseStrictNumbers()
    {
        Factory.Services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions.NumberHandling
            .ShouldBe(JsonNumberHandling.Strict);
        NachosJsonContext.Default.Options.NumberHandling.ShouldBe(JsonNumberHandling.Strict);
    }

    [Theory]
    [InlineData("Workspace")]
    [InlineData("Peer")]
    [InlineData("Session")]
    [InlineData("Message")]
    [InlineData("WorkspaceCreate")]
    [InlineData("WorkspaceUpdate")]
    [InlineData("PeerCreate")]
    [InlineData("PeerUpdate")]
    [InlineData("PeerGet")]
    [InlineData("SessionCreate")]
    [InlineData("SessionUpdate")]
    [InlineData("SessionPeerConfig")]
    [InlineData("WorkspaceConfiguration")]
    [InlineData("SessionConfiguration")]
    [InlineData("MessageConfiguration")]
    [InlineData("MessageCreate")]
    [InlineData("MessageBatchCreate")]
    [InlineData("MessageUpdate")]
    [InlineData("SummaryConfiguration")]
    [InlineData("ReasoningConfiguration")]
    [InlineData("PeerCardConfiguration")]
    [InlineData("DreamConfiguration")]
    [InlineData("DialecticConfiguration")]
    [InlineData("Page_Workspace_")]
    [InlineData("Page_Peer_")]
    [InlineData("Page_Session_")]
    [InlineData("Page_Message_")]
    public async Task Schemas_MatchManifestTypesNullabilityRequirednessAndBounds(string name)
    {
        var document = await Send(HttpMethod.Get, "/openapi/v1.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "honcho-v3-wire.json")));
        var expected = manifest.RootElement.GetProperty("schemas").GetProperty(name);
        var actual = document.GetProperty("components").GetProperty("schemas").GetProperty(SchemaName(name));
        Required(actual).Order().ShouldBe(Required(expected).Order(), name);
        var properties = actual.GetProperty("properties");
        properties.EnumerateObject().Select(p => p.Name).Order()
            .ShouldBe(expected.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order(), name);
        foreach (var property in expected.GetProperty("properties").EnumerateObject())
        {
            AssertProperty(document, name + "." + property.Name, properties.GetProperty(property.Name), property.Value);
        }
    }

    [Theory]
    [InlineData("WorkspaceCreate", "metadata")]
    [InlineData("WorkspaceCreate", "configuration")]
    [InlineData("SummaryConfiguration", "messages_per_short_summary")]
    [InlineData("SummaryConfiguration", "messages_per_long_summary")]
    [InlineData("MessageBatchCreate", "messages")]
    [InlineData("Message", "token_count")]
    [InlineData("Page_Message_", "page")]
    [InlineData("Page_Message_", "size")]
    [InlineData("Page_Message_", "total")]
    [InlineData("Page_Message_", "pages")]
    public async Task FindingFields_AreCheckedIndependently(string name, string field)
    {
        var document = await Send(HttpMethod.Get, "/openapi/v1.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "honcho-v3-wire.json")));
        AssertProperty(document, name + "." + field,
            document.GetProperty("components").GetProperty("schemas").GetProperty(SchemaName(name)).GetProperty("properties").GetProperty(field),
            manifest.RootElement.GetProperty("schemas").GetProperty(name).GetProperty("properties").GetProperty(field));
    }

    [Theory]
    [InlineData("messages_per_short_summary", 10)]
    [InlineData("messages_per_long_summary", 20)]
    public async Task SummaryNumbers_RejectStringsAndBelowMinimum_AcceptNumericMinimum(string field, int minimum)
    {
        var number = minimum.ToString(CultureInfo.InvariantCulture);
        var below = (minimum - 1).ToString(CultureInfo.InvariantCulture);
        Location(await Post("/v3/workspaces",
            $$$$"""{"id":"w","configuration":{"summary":{"{{{{field}}}}":"{{{{number}}}}"}}}""", 422),
            "body", "configuration", "summary", field);
        await Post("/v3/workspaces",
            $$$$"""{"id":"w","configuration":{"summary":{"{{{{field}}}}":{{{{below}}}}}}}""", 422);
        Ids(await Post("/v3/workspaces/list")).ShouldBeEmpty();
        var workspace = await Post("/v3/workspaces",
            $$$$"""{"id":"w","configuration":{"summary":{"{{{{field}}}}":{{{{number}}}}}}}""");
        workspace.GetProperty("configuration").GetProperty("summary").GetProperty(field).GetInt32().ShouldBe(minimum);
    }

    [Theory]
    [InlineData(0, 422)]
    [InlineData(1, 201)]
    [InlineData(100, 201)]
    [InlineData(101, 422)]
    public async Task BatchCount_MatchesDeclaredBoundsWithoutRejectedMutation(int count, int status)
    {
        await SeedSession();
        var messages = string.Join(',', Enumerable.Repeat("""{"content":"a","peer_id":"p"}""", count));
        await Post(M, "{\"messages\":[" + messages + "]}", status);
        (await Post(M + "/list")).GetProperty("total").GetInt32().ShouldBe(status == 201 ? count : 0);
        if (status == 422)
        {
            Ids(await Send(HttpMethod.Get, S + "/peers")).ShouldBeEmpty();
            Ids(await Post(W + "/peers/list")).ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task SessionCreate_AdvertisesConditionalScopeDeferralAndValidation()
    {
        var document = await Send(HttpMethod.Get, "/openapi/v1.json");
        var operation = document.GetProperty("paths").GetProperty("/v3/workspaces/{workspace_id}/sessions").GetProperty("post");
        var description = operation.GetProperty("description").GetString()!;
        description.ShouldContain("nonempty");
        description.ShouldContain("501");
        description.ShouldContain("M6");
        description.ShouldContain("mutation");
        foreach (var status in new[] { "200", "422", "501" })
        {
            operation.GetProperty("responses").TryGetProperty(status, out _).ShouldBeTrue(status);
        }
        operation.GetProperty("responses").GetProperty("422").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("oneOf").GetArrayLength().ShouldBe(2);
        var deferred = operation.GetProperty("responses").GetProperty("501").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema");
        References(deferred).ShouldContain("#/components/schemas/ErrorResponse");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    public async Task SessionCreate_NullAndEmptyScopesRemainAccepted(string scopes)
    {
        await Post("/v3/workspaces", """{"id":"w"}""");
        await Post(W + "/sessions", $$"""{"id":"s","scopes":{{scopes}}}""");
        Ids(await Post(W + "/sessions/list")).ShouldBe(["s"]);
    }

    [Fact]
    public async Task SessionCreate_NonemptyScopeDeferralMutatesNeitherSessionNorPeers()
    {
        await Post("/v3/workspaces", """{"id":"w"}""");
        await Post(W + "/sessions", """{"id":"s","peers":{"p":{}},"scopes":["later"]}""", 501);
        Ids(await Post(W + "/sessions/list")).ShouldBeEmpty();
        Ids(await Post(W + "/peers/list")).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/v3/workspaces", "200", "Workspace", false)]
    [InlineData("/v3/workspaces/{workspace_id}/peers", "200", "Peer", false)]
    [InlineData("/v3/workspaces/{workspace_id}/sessions", "200", "Session", false)]
    [InlineData("/v3/workspaces/{workspace_id}/sessions/{session_id}/messages", "201", "Message", true)]
    [InlineData("/v3/workspaces/list", "200", "PageOfWorkspace", false)]
    [InlineData("/v3/workspaces/{workspace_id}/peers/list", "200", "PageOfPeer", false)]
    [InlineData("/v3/workspaces/{workspace_id}/sessions/list", "200", "PageOfSession", false)]
    [InlineData("/v3/workspaces/{workspace_id}/sessions/{session_id}/messages/list", "200", "PageOfMessage", false)]
    public async Task SuccessfulOperations_ReferenceTheValidatedResponseShapes(string path, string status, string schema, bool array)
    {
        var document = await Send(HttpMethod.Get, "/openapi/v1.json");
        var response = document.GetProperty("paths").GetProperty(path).GetProperty("post")
            .GetProperty("responses").GetProperty(status).GetProperty("content").GetProperty("application/json").GetProperty("schema");
        if (array)
        {
            Types(document, response).ShouldBe(["array"]);
            response = response.GetProperty("items");
        }
        References(response).ShouldBe(["#/components/schemas/" + schema]);
    }

    private static string SchemaName(string name) => name.StartsWith("Page_", StringComparison.Ordinal)
        ? "PageOf" + name[5..^1] : name;

    private static void AssertProperty(JsonElement document, string name, JsonElement field, JsonElement wire)
    {
        var type = wire.TryGetProperty("type", out var declared) ? declared.GetString()! : "object";
        var nullable = wire.TryGetProperty("nullable", out var nullFlag) && nullFlag.GetBoolean();
        Types(document, field).Distinct().Order()
            .ShouldBe((nullable ? new[] { type, "null" } : [type]).Order(), name);
        foreach (var (manifestBound, schemaBound) in type switch
        {
            "array" => new[] { ("min", "minItems"), ("max", "maxItems") },
            "string" => [("min", "minLength"), ("max", "maxLength")],
            _ => [("min", "minimum"), ("max", "maximum")],
        })
        {
            if (wire.TryGetProperty(manifestBound, out var bound))
            {
                field.TryGetProperty(schemaBound, out var value).ShouldBeTrue(name + "." + schemaBound);
                value.GetDecimal().ShouldBe(bound.GetDecimal());
            }
        }
        if (wire.TryGetProperty("pattern", out var pattern))
        {
            field.GetProperty("pattern").GetString().ShouldBe(pattern.GetString());
        }
        if (wire.TryGetProperty("ref", out var reference))
        {
            var target = type switch
            {
                "array" => field.GetProperty("items"),
                "object" when declared.ValueKind == JsonValueKind.String => field.GetProperty("additionalProperties"),
                _ => field,
            };
            References(target).ShouldContain("#/components/schemas/" + reference.GetString());
        }
    }

    private static IEnumerable<string> Required(JsonElement schema) =>
        schema.TryGetProperty("required", out var required)
            ? required.EnumerateArray().Select(value => value.GetString()!) : [];

    private static JsonElement[] Alternatives(JsonElement schema) =>
        schema.TryGetProperty("oneOf", out var union) || schema.TryGetProperty("anyOf", out union)
            ? union.EnumerateArray().ToArray() : [];

    private static IEnumerable<string> References(JsonElement schema) =>
        schema.TryGetProperty("$ref", out var reference)
            ? [reference.GetString()!] : Alternatives(schema).SelectMany(References);

    private static IEnumerable<string> Types(JsonElement document, JsonElement schema)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            const string prefix = "#/components/schemas/";
            var path = reference.GetString()!;
            path.ShouldStartWith(prefix);
            return Types(document, document.GetProperty("components").GetProperty("schemas").GetProperty(path[prefix.Length..]));
        }
        if (schema.TryGetProperty("type", out var type))
        {
            return type.ValueKind == JsonValueKind.String
                ? [type.GetString()!] : type.EnumerateArray().Select(value => value.GetString()!);
        }
        var alternatives = Alternatives(schema);
        alternatives.ShouldNotBeEmpty("A schema must declare its type, a reference, or a union.");
        return alternatives.SelectMany(value => Types(document, value));
    }
}
