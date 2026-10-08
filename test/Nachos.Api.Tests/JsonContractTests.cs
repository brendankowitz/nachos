using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nachos.Api.Json;
using Shouldly;

namespace Nachos.Api.Tests;

public sealed class JsonContractTests : ApiTest
{
    [Fact]
    public void HttpSerializer_IsSourceGeneratedSnakeCaseWithExplicitNulls()
    {
        var options = Factory.Services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        options.GetTypeInfo(typeof(Nachos.Abstractions.Contracts.SessionPeerConfig)).OriginatingResolver
            .ShouldBeOfType<NachosJsonContext>();
        options.PropertyNamingPolicy.ShouldBe(JsonNamingPolicy.SnakeCaseLower);
        options.DefaultIgnoreCondition.ShouldBe(JsonIgnoreCondition.Never);
        JsonSerializer.Serialize(new Nachos.Abstractions.Contracts.SessionPeerConfig(), options)
            .ShouldBe("""{"observe_me":null,"observe_others":null}""");
    }

    [Fact]
    public async Task OpenApi_ImplementedRoutesAdvertiseManifestQueryAndStatuses()
    {
        var openApi = await Send(HttpMethod.Get, "/openapi/v1.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "honcho-v3-wire.json")));
        foreach (var route in manifest.RootElement.GetProperty("routes").EnumerateArray())
        {
            var path = route.GetProperty("path").GetString()!;
            var method = route.GetProperty("method").GetString()!.ToLowerInvariant();
            if (path == "/health")
            {
                continue;
            }
            var operation = openApi.GetProperty("paths").GetProperty(path).GetProperty(method);
            var responses = operation.GetProperty("responses");
            if (responses.TryGetProperty("501", out _))
            {
                continue;
            }
            var query = operation.TryGetProperty("parameters", out var parameters)
                ? parameters.EnumerateArray().Where(p => p.GetProperty("in").GetString() == "query")
                    .Select(p => p.GetProperty("name").GetString()!).Order().ToArray()
                : [];
            query.ShouldBe(route.GetProperty("query").EnumerateArray().Select(p => p.GetString()!).Order());
            foreach (var status in route.GetProperty("statuses").EnumerateArray())
            {
                responses.TryGetProperty(status.GetRawText(), out _).ShouldBeTrue(path + " " + status);
            }
            if (query.Contains("page"))
            {
                var schemas = parameters.EnumerateArray().Where(p => p.GetProperty("in").GetString() == "query")
                    .ToDictionary(p => p.GetProperty("name").GetString()!, p => p.GetProperty("schema"));
                schemas["page"].GetProperty("default").GetInt32().ShouldBe(1);
                schemas["size"].GetProperty("default").GetInt32().ShouldBe(50);
                schemas["size"].GetProperty("maximum").GetInt32().ShouldBe(100);
            }
        }
    }

    [Fact]
    public async Task OpenApi_DtoFieldsMatchManifest()
    {
        var openApi = await Send(HttpMethod.Get, "/openapi/v1.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "honcho-v3-wire.json")));
        var schemas = openApi.GetProperty("components").GetProperty("schemas");
        string[] names =
        [
            "Workspace", "Peer", "Session", "Message", "WorkspaceCreate", "WorkspaceUpdate",
            "PeerCreate", "PeerUpdate", "PeerGet", "SessionCreate", "SessionUpdate", "SessionPeerConfig",
            "WorkspaceConfiguration", "SessionConfiguration", "MessageConfiguration", "MessageCreate",
            "MessageBatchCreate", "MessageUpdate", "SummaryConfiguration", "ReasoningConfiguration",
            "PeerCardConfiguration", "DreamConfiguration", "DialecticConfiguration",
        ];
        foreach (var name in names)
        {
            schemas.GetProperty(name).GetProperty("properties").EnumerateObject().Select(p => p.Name).Order()
                .ShouldBe(manifest.RootElement.GetProperty("schemas").GetProperty(name)
                    .GetProperty("properties").EnumerateObject().Select(p => p.Name).Order(), name);
        }
    }

    [Fact]
    public async Task RealResponses_FieldsAndJsonTypesMatchManifest()
    {
        await SeedSession();
        var workspace = await Post("/v3/workspaces", """{"id":"w"}""");
        var peer = await Post(W + "/peers", """{"id":"p"}""");
        var session = await Post(W + "/sessions", """{"id":"s"}""");
        var messages = await Post(M, """{"messages":[{"content":"hello world","peer_id":"p"}]}""", 201);
        var page = await Post(M + "/list");
        var config = await Send(HttpMethod.Get, S + "/peers/p/config");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "honcho-v3-wire.json")));
        foreach (var (name, value) in new (string, JsonElement)[]
        {
            ("Workspace", workspace), ("Peer", peer), ("Session", session),
            ("Message", messages[0]), ("Page_Message_", page), ("SessionPeerConfig", config),
        })
        {
            var schema = manifest.RootElement.GetProperty("schemas").GetProperty(name).GetProperty("properties");
            value.EnumerateObject().Select(p => p.Name).Order().ShouldBe(schema.EnumerateObject().Select(p => p.Name).Order());
            foreach (var property in schema.EnumerateObject())
            {
                var actual = value.GetProperty(property.Name);
                if (actual.ValueKind == JsonValueKind.Null)
                {
                    property.Value.GetProperty("nullable").GetBoolean().ShouldBeTrue();
                }
                else if (property.Value.TryGetProperty("type", out var type))
                {
                    var expected = type.GetString() switch
                    {
                        "object" => JsonValueKind.Object,
                        "array" => JsonValueKind.Array,
                        "integer" or "number" => JsonValueKind.Number,
                        "boolean" => actual.GetBoolean() ? JsonValueKind.True : JsonValueKind.False,
                        "string" => JsonValueKind.String,
                        _ => throw new InvalidOperationException("Unknown manifest scalar type."),
                    };
                    actual.ValueKind.ShouldBe(expected, name + "." + property.Name);
                }
            }
        }
    }
}
