using System.Text.Json;
using Shouldly;

namespace Nachos.Api.Tests;

/// <summary>
/// <c>DELETE S</c> and <c>DELETE W</c>: 202 with no body (Honcho's OpenAPI declares 202 with an unconstrained schema and
/// documents no body), the 409 for a workspace with an active session (Honcho's DELETE W description: "Returns 409
/// Conflict if the workspace contains active sessions"), and the write barrier. The worker loops are off, so every job
/// stays pending and the barrier is observable.
/// </summary>
public sealed class DeletionEndpointTests : ApiTest
{
    private static void Conflict(JsonElement body, string detail)
    {
        Problem(body, 409, JsonValueKind.String);
        body.GetProperty("detail").GetString().ShouldBe(detail);
    }

    private async Task<JsonElement[]> Jobs() =>
        [.. (await Send(HttpMethod.Get, W + "/jobs")).GetProperty("items").EnumerateArray()];

    [Fact]
    public async Task DeleteSession_Is202WithNoBody_AndTheSessionIsInactive()
    {
        await SeedSession();

        await Send(HttpMethod.Delete, S, status: 202);

        var session = (await Post(W + "/sessions/list")).GetProperty("items").EnumerateArray().Single();
        session.GetProperty("id").GetString().ShouldBe("s");
        session.GetProperty("is_active").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task DeleteSession_Repeated_Is202_AndNamesTheSameJob()
    {
        await SeedSession();

        await Send(HttpMethod.Delete, S, status: 202);
        await Send(HttpMethod.Delete, S, status: 202);

        var job = (await Jobs()).Single();
        job.GetProperty("type").GetString().ShouldBe("deletion");
        job.GetProperty("target").GetProperty("kind").GetString().ShouldBe("session");
        job.GetProperty("target").GetProperty("id").GetString().ShouldBe("s");
    }

    [Fact]
    public async Task DeleteSession_WritesUnderItAre409()
    {
        await SeedSession();
        await Send(HttpMethod.Delete, S, status: 202);

        const string detail = "session is being deleted";
        Conflict(await Post(M, """{"messages":[{"content":"late","peer_id":"alice"}]}""", 409), detail);
        Conflict(await Send(HttpMethod.Put, S, """{"metadata":{"a":1}}""", 409), detail);
        Conflict(await Post(S + "/peers", """{"alice":{}}""", 409), detail);
    }

    [Fact]
    public async Task DeleteSession_Missing_Is404()
    {
        await Send(HttpMethod.Delete, S, status: 404);
        await Post("/v3/workspaces", """{"id":"w"}""");
        await Send(HttpMethod.Delete, S, status: 404);
    }

    [Fact]
    public async Task DeleteWorkspace_WithAnActiveSession_Is409_AndChangesNothing()
    {
        await SeedSession();

        Conflict(await Send(HttpMethod.Delete, W, status: 409), "workspace has active sessions");

        await Send(HttpMethod.Put, W, """{"metadata":{"still":"writable"}}""");
        (await Jobs()).ShouldBeEmpty();
    }

    [Fact]
    public async Task DeleteWorkspace_AfterItsSessionsAreDeleted_Is202_AndWritesAre409()
    {
        await SeedSession();
        await Send(HttpMethod.Delete, S, status: 202);

        await Send(HttpMethod.Delete, W, status: 202);
        await Send(HttpMethod.Delete, W, status: 202);

        const string detail = "workspace is being deleted";
        Conflict(await Send(HttpMethod.Put, W, """{"metadata":{"a":1}}""", 409), detail);
        Conflict(await Post(W + "/peers", """{"id":"late"}""", 409), detail);
        Conflict(await Post(W + "/sessions", """{"id":"late"}""", 409), detail);
        Conflict(await Send(HttpMethod.Delete, S, status: 409), detail);
        (await Jobs()).Select(job => job.GetProperty("target").GetProperty("kind").GetString())
            .ShouldBe(["session", "workspace"]);
    }

    [Fact]
    public async Task DeleteWorkspace_Missing_Is404()
    {
        await Send(HttpMethod.Delete, W, status: 404);
    }

    [Theory]
    [InlineData("/v3/workspaces/{workspace_id}")]
    [InlineData("/v3/workspaces/{workspace_id}/sessions/{session_id}")]
    public async Task OpenApi_Documents202And409(string path)
    {
        var responses = (await Send(HttpMethod.Get, "/openapi/v1.json")).GetProperty("paths").GetProperty(path)
            .GetProperty("delete").GetProperty("responses");

        responses.EnumerateObject().Select(response => response.Name).ShouldBe(["202", "409", "422"], ignoreOrder: true);
        responses.GetProperty("202").TryGetProperty("content", out _).ShouldBeFalse();
    }
}
