using System.Text.Json;
using Shouldly;

namespace Nachos.Api.Tests;

public sealed class QueueEndpointTests : ApiTest
{
    private const string Status = W + "/queue/status";

    [Fact]
    public async Task Status_CountsAddUp_AndSessionsIsAMap()
    {
        await SeedSession();
        await Post(W + "/sessions/s/messages", """{"messages":[{"content":"I like tea","peer_id":"alice"}]}""", 201);

        foreach (var path in new[] { Status, Status + "?observer_id=alice&sender_id=alice&session_id=s" })
        {
            var status = await Send(HttpMethod.Get, path);
            var total = status.GetProperty("total_work_units").GetInt64();
            (status.GetProperty("completed_work_units").GetInt64() + status.GetProperty("in_progress_work_units").GetInt64() +
                status.GetProperty("pending_work_units").GetInt64()).ShouldBe(total);
            status.GetProperty("sessions").ValueKind.ShouldBe(JsonValueKind.Object);
        }
    }

    [Fact]
    public async Task Status_OfAnEmptyWorkspace_IsZero()
    {
        await Post("/v3/workspaces", """{"id":"w"}""");
        var status = await Send(HttpMethod.Get, Status + "?session_id=nothing");
        status.GetProperty("total_work_units").GetInt64().ShouldBe(0);
        status.GetProperty("sessions").EnumerateObject().ShouldBeEmpty();
    }

    [Fact]
    public async Task Status_InvalidFilterId_Is422()
    {
        await Post("/v3/workspaces", """{"id":"w"}""");
        Problem(await Send(HttpMethod.Get, Status + "?observer_id=bad%20id", status: 422), 422, JsonValueKind.String);
    }
}
