using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nachos.Core.Configuration;
using Nachos.Core.Tokens;
using Shouldly;

namespace Nachos.Api.Tests;

public sealed class ErrorShapeTests : ApiTest
{
    [Fact]
    public async Task Validation_Is422DetailArray()
    {
        Location(await Post("/v3/workspaces", "{}", 422), "body", "id");
    }

    [Fact]
    public async Task Domain_Is422DetailString()
    {
        var instructions = string.Concat(Enumerable.Repeat(" hello", 3000));
        var counter = Factory.Services.GetRequiredService<ITokenCounter>();
        var limit = Factory.Services.GetRequiredService<IOptions<NachosOptions>>().Value.Deriver.MaxCustomInstructionsTokens;
        counter.Count(instructions).ShouldBeGreaterThan(limit);
        Problem(await Post("/v3/workspaces",
            JsonSerializer.Serialize(new { id = "w", configuration = new { custom_instructions = instructions } }), 422),
            422, JsonValueKind.String);
        Ids(await Post("/v3/workspaces/list")).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("{")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    public async Task MalformedBody_IsLocated422(string body)
    {
        Location(await Post("/v3/workspaces", body, 422), "body");
    }

    [Theory]
    [InlineData("""{"id":7}""", "id")]
    [InlineData("""{"id":"w","metadata":[]}""", "metadata")]
    [InlineData("""{"id":"w","configuration":false}""", "configuration")]
    public async Task WrongPropertyType_IsLocated422(string body, string field)
    {
        Location(await Post("/v3/workspaces", body, 422), "body", field);
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("configuration")]
    public async Task WorkspaceCreate_ExplicitNullForNonnullableField_Is422(string field)
    {
        Location(await Post("/v3/workspaces", $$"""{"id":"w","{{field}}":null}""", 422), "body", field);
    }

    [Theory]
    [InlineData("PUT", W + "/peers/missing", "{}")]
    [InlineData("POST", W + "/peers/missing/sessions", "{}")]
    [InlineData("PUT", W + "/sessions/missing", "{}")]
    [InlineData("GET", S + "/peers/missing/config", null)]
    [InlineData("POST", "/v3/workspaces/missing/sessions", """{"id":"s"}""")]
    public async Task MissingResources_Are404(string method, string path, string? body)
    {
        await SeedSession();
        Problem(await Send(new HttpMethod(method), path, body, 404), 404, JsonValueKind.String);
    }

    [Fact]
    public async Task MalformedUnicode_InTypedId_Is422()
    {
        Location(await Post("/v3/workspaces", """{"id":"\uD800"}""", 422), "body", "id");
    }

    [Theory]
    [InlineData("POST", W + "/peers/list", """{"kind":"\uD800"}""")]
    [InlineData("POST", S + "/peers", """{"\uD800":{}}""")]
    [InlineData("DELETE", S + "/peers", """["\uD800"]""")]
    public async Task MalformedUnicode_InTypedMembershipInput_Is422(string method, string path, string body)
    {
        await SeedSession();
        Problem(await Send(new HttpMethod(method), path, body, 422), 422, JsonValueKind.Array);
    }

    [Fact]
    public async Task WrongNestedConfigurationType_HasFullLocation()
    {
        Location(await Post("/v3/workspaces",
            """{"id":"w","configuration":{"summary":{"enabled":"no"}}}""", 422),
            "body", "configuration", "summary", "enabled");
    }

    [Fact]
    public async Task Every4xx_HasRfc9457Fields()
    {
        Problem(await Send(HttpMethod.Get, "/v3/does-not-exist", status: 404), 404, JsonValueKind.String);
        Problem(await Send(HttpMethod.Get, "/v3/workspaces", status: 405), 405, JsonValueKind.String);
        Problem(await Send(HttpMethod.Put, W, "{}", 404), 404, JsonValueKind.String);
    }
}
