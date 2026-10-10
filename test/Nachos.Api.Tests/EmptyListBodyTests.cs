using System.Net.Http.Headers;
using System.Text.Json;
using Shouldly;

namespace Nachos.Api.Tests;

public sealed class EmptyListBodyTests : ApiTest
{
    private static readonly (string Path, string Id)[] Lists =
    [
        ("/v3/workspaces/list", "w"),
        (W + "/peers/list", "p"),
        (W + "/sessions/list", "s"),
        (W + "/peers/p/sessions", "s"),
        (M + "/list", "message"),
    ];

    public static IEnumerable<object[]> ListRoutes() =>
        Lists.Select(route => new object[] { route.Path, route.Id });

    [Theory]
    [MemberData(nameof(ListRoutes))]
    public async Task ZeroBytes_MatchExplicitEmptyObjectAndReturnRealResources(string path, string expectedId)
    {
        await SeedLists();
        var explicitObject = await Post(path);
        var empty = await SendBytes(path, [], 200);
        empty.GetRawText().ShouldBe(explicitObject.GetRawText());
        empty.GetProperty("total").GetInt32().ShouldBe(1);
        if (expectedId == "message")
            empty.GetProperty("items")[0].GetProperty("content").GetString().ShouldBe("visible");
        else
            Ids(empty).ShouldBe([expectedId]);
    }

    public static IEnumerable<object[]> NonemptyInvalidBodies()
    {
        foreach (var (path, _) in Lists)
        foreach (var (hex, type) in new[]
        {
            ("20", "json_invalid"),
            ("0D0A09", "json_invalid"),
            ("7B", "json_invalid"),
            ("FF", "json_invalid"),
            ("EFBBBF", "json_invalid"),
            ("6E756C6C", "model_type"),
            ("5B5D", "model_type"),
            ("2222", "model_type"),
            ("3432", "model_type"),
        })
            yield return [path, hex, type];
    }

    [Theory]
    [MemberData(nameof(NonemptyInvalidBodies))]
    public async Task NonzeroBody_IsNeverTreatedAsAnAbsentFilter(string path, string hex, string type)
    {
        await SeedLists();
        var error = await SendBytes(path, Convert.FromHexString(hex), 422);
        Location(error, "body");
        error.GetProperty("detail")[0].GetProperty("type").GetString().ShouldBe(type);
        (await Post(path)).GetProperty("total").GetInt32().ShouldBe(1);
    }

    [Theory]
    [InlineData("/v3/workspaces")]
    [InlineData(W + "/peers")]
    [InlineData(W + "/sessions")]
    [InlineData(M)]
    public async Task ZeroBytes_OnCreateRoutesRemainInvalidJson(string path)
    {
        await SeedLists();
        var error = await SendBytes(path, [], 422);
        Location(error, "body");
        error.GetProperty("detail")[0].GetProperty("type").GetString().ShouldBe("json_invalid");
    }

    [Theory]
    [MemberData(nameof(ListRoutes))]
    public async Task NonemptyFilters_AreStillApplied(string path, string expectedId)
    {
        await SeedLists();
        var actual = await Post(path, """{"filters":{"metadata":{"selected":false}}}""");
        actual.GetProperty("total").GetInt32().ShouldBe(0, expectedId);
    }

    private async Task SeedLists()
    {
        await Post("/v3/workspaces", """{"id":"w","metadata":{"selected":true}}""");
        await Post(W + "/peers", """{"id":"p","metadata":{"selected":true}}""");
        await Post(W + "/sessions", """{"id":"s","metadata":{"selected":true},"peers":{"p":{}}}""");
        await Post(M, """{"messages":[{"content":"visible","peer_id":"p","metadata":{"selected":true}}]}""", 201);
    }

    private async Task<JsonElement> SendBytes(string path, byte[] bytes, int status)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Content.Headers.ContentLength = bytes.Length;
        using var response = await Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        ((int)response.StatusCode).ShouldBe(status, text);
        using var json = JsonDocument.Parse(text);
        return json.RootElement.Clone();
    }
}
