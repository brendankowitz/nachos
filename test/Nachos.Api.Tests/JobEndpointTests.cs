using System.Text.Json;
using Shouldly;

namespace Nachos.Api.Tests;

/// <summary><c>GET W/jobs</c> (Nachos extension, plan D6). The worker loops are off, so every job stays pending.</summary>
public sealed class JobEndpointTests : ApiTest
{
    private const string Jobs = W + "/jobs";

    private async Task SeedDeletedSessions(params string[] sessions)
    {
        await Post("/v3/workspaces", """{"id":"w"}""");
        foreach (var session in sessions)
        {
            await Post(W + "/sessions", $$"""{"id":"{{session}}"}""");
            await Send(HttpMethod.Delete, W + "/sessions/" + session, status: 202);
        }
    }

    [Fact]
    public async Task Jobs_OfAWorkspaceWithoutDeletions_IsAnEmptyPage()
    {
        await Post("/v3/workspaces", """{"id":"w"}""");

        var page = await Send(HttpMethod.Get, Jobs);

        page.GetProperty("items").GetArrayLength().ShouldBe(0);
        page.GetProperty("total").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Jobs_AreListedInTheWireShape()
    {
        await SeedDeletedSessions("s1");

        var job = (await Send(HttpMethod.Get, Jobs)).GetProperty("items").EnumerateArray().Single();

        job.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["id", "type", "target", "status", "attempts", "error", "created_at", "completed_at"]);
        long.Parse(job.GetProperty("id").GetString()!, System.Globalization.CultureInfo.InvariantCulture).ShouldBePositive();
        job.GetProperty("type").GetString().ShouldBe("deletion");
        job.GetProperty("target").GetProperty("kind").GetString().ShouldBe("session");
        job.GetProperty("target").GetProperty("id").GetString().ShouldBe("s1");
        job.GetProperty("status").GetString().ShouldBe("pending");
        job.GetProperty("attempts").GetInt32().ShouldBe(0);
        job.GetProperty("error").ValueKind.ShouldBe(JsonValueKind.Null);
        job.GetProperty("created_at").ValueKind.ShouldBe(JsonValueKind.String);
        job.GetProperty("completed_at").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Jobs_ArePagedOldestFirst_AndReversible()
    {
        await SeedDeletedSessions("s1", "s2", "s3");

        static string[] Targets(JsonElement page) =>
            [.. page.GetProperty("items").EnumerateArray().Select(job => job.GetProperty("target").GetProperty("id").GetString()!)];

        var second = await Send(HttpMethod.Get, Jobs + "?page=2&size=1");
        Targets(second).ShouldBe(["s2"]);
        second.GetProperty("total").GetInt32().ShouldBe(3);
        second.GetProperty("pages").GetInt32().ShouldBe(3);
        Targets(await Send(HttpMethod.Get, Jobs + "?reverse=true")).ShouldBe(["s3", "s2", "s1"]);
    }

    [Theory]
    [InlineData("size=0")]
    [InlineData("size=101")]
    [InlineData("page=0")]
    [InlineData("cursor=")]
    public async Task Jobs_InvalidPaging_Is422(string query)
    {
        await Post("/v3/workspaces", """{"id":"w"}""");

        await Send(HttpMethod.Get, Jobs + "?" + query, status: 422);
    }

    [Fact]
    public async Task Jobs_OfAMissingWorkspace_Is404()
    {
        await Send(HttpMethod.Get, Jobs, status: 404);
    }
}
