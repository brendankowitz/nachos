using System.Text.Json;
using Shouldly;

namespace Nachos.Api.Tests;

/// <summary>
/// Honcho 3.6.0 options whose features Nachos lacks (cursor pagination; scopes are M6) fail closed with a 422 instead of
/// being ignored, while their default values are accepted.
/// </summary>
public sealed class UnsupportedOptionTests : ApiTest
{
    public static TheoryData<string, string> ListRoutes() => new()
    {
        { "POST", "/v3/workspaces/list" },
        { "POST", W + "/peers/list" },
        { "POST", W + "/peers/p/sessions" },
        { "POST", W + "/sessions/list" },
        { "GET", S + "/peers" },
        { "POST", M + "/list" },
        { "POST", W + "/conclusions/list" },
    };

    [Theory]
    [MemberData(nameof(ListRoutes))]
    public async Task Cursor_AnyValueIncludingTheEmptyOptIn_Is422(string method, string path)
    {
        await SeedSession();
        await Post(W + "/peers", """{"id":"p"}""");
        foreach (var query in new[] { "?cursor=", "?cursor=abc", "?page=1&cursor=" })
        {
            Unsupported(await Send(new HttpMethod(method), path + query, method == "GET" ? null : "{}", 422), "query", "cursor");
        }
        await Send(new HttpMethod(method), path, method == "GET" ? null : "{}");
    }

    [Theory]
    [InlineData("?include_scopes=true", "{}", "query", "include_scopes")]
    [InlineData("", """{"scope":"team"}""", "body", "scope")]
    [InlineData("", """{"in_no_scope":true}""", "body", "in_no_scope")]
    public async Task SessionList_ScopeOptions_Are422(string query, string body, string source, string option)
    {
        await SeedSession();
        Unsupported(await Post(W + "/sessions/list" + query, body, 422), source, option);
    }

    public static TheoryData<string, string, object[]> ConclusionFilters() => new()
    {
        { "/list", """{"filters":{"source_message_ids":["m1"]}}""", ["body", "filters", "source_message_ids"] },
        { "/list", """{"filters":{"source_ids":["c1"]}}""", ["body", "filters", "source_ids"] },
        { "/list", """{"filters":{"level":"explicit"}}""", ["body", "filters", "level"] },
        { "/list", """{"filters":{"times_derived":{"gte":2}}}""", ["body", "filters", "times_derived"] },
        { "/list", """{"filters":{"OR":[{"id":"x"},{"source_message_ids":["m1"]}]}}""", ["body", "filters", "OR", 1, "source_message_ids"] },
        { "/query", """{"query":"tea","filters":{"source_message_ids":["m1"]}}""", ["body", "filters", "source_message_ids"] },
        { "/query", """{"query":"tea","filters":{"NOT":[{"level":"explicit"}]}}""", ["body", "filters", "NOT", 0, "level"] },
    };

    /// <summary>Documented conclusion filter fields Nachos does not filter on are rejected, never ignored (matching all).</summary>
    [Theory]
    [MemberData(nameof(ConclusionFilters))]
    public async Task ConclusionFilter_DocumentedButUnsupportedField_IsLocated422(string route, string body, object[] location)
    {
        await SeedSession();
        await Post(W + "/peers", """{"id":"p"}""");
        await Post(W + "/conclusions", """{"conclusions":[{"content":"likes tea","observer_id":"p","observed_id":"p"}]}""", 201);

        Unsupported(await Post(W + "/conclusions" + route, body, 422), location);
    }

    private static void Unsupported(JsonElement body, params object[] location)
    {
        Location(body, location);
        var error = body.GetProperty("detail")[0];
        error.GetProperty("type").GetString().ShouldBe("unsupported");
        error.GetProperty("msg").GetString().ShouldBe(location[^1] + " is not supported in this Nachos version");
    }

    [Theory]
    [InlineData("?include_scopes=false", "{}")]
    [InlineData("", """{"scope":null,"in_no_scope":false}""")]
    public async Task SessionList_ScopeOptionDefaults_AreAccepted(string query, string body)
    {
        await SeedSession();
        var page = await Post(W + "/sessions/list" + query, body);
        Ids(page).ShouldBe(["s"]);
        page.GetProperty("items")[0].GetProperty("scopes").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"yes\"")]
    [InlineData("1")]
    public async Task SessionList_NonBooleanInNoScope_Is422AtTheField(string value)
    {
        await SeedSession();
        Location(await Post(W + "/sessions/list", "{\"in_no_scope\":" + value + "}", 422), "body", "in_no_scope");
    }
}
