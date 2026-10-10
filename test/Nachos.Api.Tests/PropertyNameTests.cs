using Shouldly;

namespace Nachos.Api.Tests;

public sealed class PropertyNameTests : ApiTest
{
    public static IEnumerable<object[]> TypedBodies()
    {
        (string Method, string Path, string Fields, int Status)[] routes =
        [
            ("POST", "/v3/workspaces", "\"id\":\"new\"", 200),
            ("POST", W + "/peers", "\"id\":\"new\"", 200),
            ("POST", W + "/sessions", "\"id\":\"new\"", 200),
            ("POST", "/v3/workspaces/list", "\"filters\":null", 200),
            ("POST", W + "/peers/list", "\"filters\":null", 200),
            ("POST", W + "/sessions/list", "\"filters\":null", 200),
            ("POST", W + "/peers/p/sessions", "\"filters\":null", 200),
            ("POST", M + "/list", "\"filters\":null", 200),
            ("PUT", W, "\"metadata\":{\"changed\":true}", 200),
            ("PUT", W + "/peers/p", "\"metadata\":{\"changed\":true}", 200),
            ("PUT", S, "\"metadata\":{\"changed\":true}", 200),
            ("PUT", M + "/{message}", "\"metadata\":{\"changed\":true}", 200),
            ("PUT", S + "/peers/p/config", "\"observe_me\":false", 204),
        ];
        foreach (var (method, path, fields, status) in routes)
        {
            foreach (var before in new[] { true, false })
            {
                yield return [method, path, fields, status, before];
            }
        }
    }

    [Theory]
    [MemberData(nameof(TypedBodies))]
    public async Task MalformedRootName_BeforeOrAfterKnownField_IsLocated422(
        string method, string path, string fields, int successStatus, bool before)
    {
        path = await Prepare(path);
        var json = Envelope(fields, "\"\\uD800\":\"private-value\"", before);
        var error = await Send(new HttpMethod(method), path, json, 422);
        Location(error, "body");
        error.GetProperty("status").GetInt32().ShouldNotBe(successStatus);
        error.GetProperty("detail")[0].GetProperty("type").GetString().ShouldBe("json_invalid");
        error.GetRawText().ShouldNotContain("private-value");
        Ids(await Post("/v3/workspaces/list")).ShouldNotContain("new");
        Ids(await Post(W + "/peers/list")).ShouldNotContain("new");
        Ids(await Post(W + "/sessions/list")).ShouldNotContain("new");
        (await Post("/v3/workspaces", """{"id":"w"}""")).GetProperty("metadata")
            .TryGetProperty("changed", out _).ShouldBeFalse();
        (await Post(W + "/peers", """{"id":"p"}""")).GetProperty("metadata")
            .TryGetProperty("changed", out _).ShouldBeFalse();
        (await Post(W + "/sessions", """{"id":"s"}""")).GetProperty("metadata")
            .TryGetProperty("changed", out _).ShouldBeFalse();
        foreach (var message in (await Post(M + "/list")).GetProperty("items").EnumerateArray())
        {
            message.GetProperty("metadata").TryGetProperty("changed", out _).ShouldBeFalse();
        }
        (await Send(HttpMethod.Get, S + "/peers/p/config")).GetProperty("observe_me").ValueKind
            .ShouldBe(System.Text.Json.JsonValueKind.Null);
    }

    [Theory]
    [MemberData(nameof(TypedBodies))]
    public async Task ValidSurrogatePairUnknownName_RemainsIgnored(
        string method, string path, string fields, int successStatus, bool before)
    {
        path = await Prepare(path);
        var json = Envelope(fields, "\"\\uD83D\\uDE00\":{\"a\":1e2,\"a\":100,\"n\":null}", before);
        await Send(new HttpMethod(method), path, json, successStatus);
    }

    [Theory]
    [InlineData("\\uD800")]
    [InlineData("\\uDC00")]
    [InlineData("\\uD800x")]
    public async Task MalformedRootName_DoesNotDependOnSurrogateVariant(string name)
    {
        Location(await Post("/v3/workspaces", $$"""{"id":"new","{{name}}":null}""", 422), "body");
    }

    [Theory]
    [InlineData("peer.x[2]")]
    [InlineData("2147483648")]
    public async Task PeerDictionary_LiteralErrorLocationsRemainUnchanged(string name)
    {
        await SeedSession();
        Location(await Post(S + "/peers", $$$"""{"{{{name}}}":{"observe_me":"no"}}""", 422),
            "body", name, "observe_me");
    }

    private async Task<string> Prepare(string path)
    {
        await SeedSession();
        await Post(S + "/peers", """{"p":{}}""");
        if (path.Contains("{message}", StringComparison.Ordinal))
        {
            var messages = await Post(M, """{"messages":[{"content":"a","peer_id":"p"}]}""", 201);
            return path.Replace("{message}", messages[0].GetProperty("id").GetString(), StringComparison.Ordinal);
        }
        return path;
    }

    private static string Envelope(string fields, string unknown, bool before) =>
        before ? "{" + unknown + "," + fields + "}" : "{" + fields + "," + unknown + "}";
}
