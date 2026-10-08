using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nachos.Core.Configuration;
using Shouldly;

namespace Nachos.Api.Tests;

public sealed class WorkspaceEndpointsTests : ApiTest
{
    [Fact]
    public async Task GetOrCreate_Twice_Returns200SameBody()
    {
        var first = await Post("/v3/workspaces", """{"id":"w","metadata":{"n":1}}""");
        var second = await Post("/v3/workspaces", """{"id":"w","metadata":{"n":2}}""");
        second.GetRawText().ShouldBe(first.GetRawText());
        first.GetProperty("created_at").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UnixEpoch);
        first.EnumerateObject().Select(p => p.Name).Order().ShouldBe(["configuration", "created_at", "id", "metadata"]);
    }

    [Fact]
    public async Task Update_NullConfiguration_Unchanged()
    {
        var first = await Post("/v3/workspaces",
            """{"id":"w","configuration":{"reasoning":{"enabled":false}}}""");
        var updated = await Send(HttpMethod.Put, W, """{"configuration":null,"metadata":{"tag":"new"}}""");
        updated.GetProperty("configuration").GetRawText().ShouldBe(first.GetProperty("configuration").GetRawText());
        updated.GetProperty("metadata").GetProperty("tag").GetString().ShouldBe("new");
        updated.GetProperty("configuration").GetProperty("custom_instructions").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task List_WithMetadataFilter()
    {
        await Post("/v3/workspaces", """{"id":"w","metadata":{"tag":"yes"}}""");
        await Post("/v3/workspaces", """{"id":"other","metadata":{"tag":"no"}}""");
        var page = await Post("/v3/workspaces/list", """{"filters":{"metadata":{"tag":"yes"}}}""");
        Ids(page).ShouldBe(["w"]);
    }

    [Fact]
    public async Task StoredInstructions_LoweredBudget_ReadsUnchanged()
    {
        const string instructions = "hello world";
        await Post("/v3/workspaces", """{"id":"w","configuration":{"custom_instructions":"hello world"}}""");
        Factory.Services.GetRequiredService<IOptions<NachosOptions>>().Value.Deriver.MaxCustomInstructionsTokens = 1;
        var page = await Post("/v3/workspaces/list");
        page.GetProperty("items")[0].GetProperty("configuration").GetProperty("custom_instructions")
            .GetString().ShouldBe(instructions);
        var unchanged = await Send(HttpMethod.Put, W, """{"metadata":{"updated":true}}""");
        unchanged.GetProperty("configuration").GetProperty("custom_instructions").GetString().ShouldBe(instructions);
        var rejected = await Send(HttpMethod.Put, W,
            """{"configuration":{"custom_instructions":"hello world"}}""", 422);
        Problem(rejected, 422, JsonValueKind.String);
    }

    [Theory]
    [InlineData("""{"id":"w","configuration":{"summary":{"messages_per_short_summary":9}}}""", "messages_per_short_summary")]
    [InlineData("""{"id":"w","configuration":{"summary":{"messages_per_long_summary":19}}}""", "messages_per_long_summary")]
    public async Task ConfigurationAdmission_PreservesFullLocation(string body, string field)
    {
        Location(await Post("/v3/workspaces", body, 422), "body", "configuration", "summary", field);
    }
}
