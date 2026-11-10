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
            var body = await Send(new HttpMethod(method), path + query, method == "GET" ? null : "{}", 422);
            body.GetProperty("detail").GetString().ShouldBe("cursor is not supported in this Nachos version");
        }
        await Send(new HttpMethod(method), path, method == "GET" ? null : "{}");
    }

    [Theory]
    [InlineData("?include_scopes=true", "{}", "include_scopes")]
    [InlineData("", """{"scope":"team"}""", "scope")]
    [InlineData("", """{"in_no_scope":true}""", "in_no_scope")]
    public async Task SessionList_ScopeOptions_Are422(string query, string body, string option)
    {
        await SeedSession();
        var problem = await Post(W + "/sessions/list" + query, body, 422);
        problem.GetProperty("detail").GetString().ShouldBe(option + " is not supported in this Nachos version");
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
