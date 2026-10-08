using System.Text.Json;
using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Json;
using Nachos.Testing;
using Shouldly;

namespace Nachos.Client.Tests;

/// <summary>
/// Metadata may be <see cref="StrictJsonData.DefaultMaxDepth"/> (64) containers deep, the metadata object included.
/// The client's request and response envelopes add up to three more levels (a page, its <c>items</c>, the entity; a
/// batch, its <c>messages</c>, the entry), so the client must read and write past 64 while staying bounded. These round
/// trips run against the real server and compare with the in-process client.
/// </summary>
public sealed class MetadataDepthRoundTripTests
{
    private static readonly JsonSerializerOptions Compare = new() { MaxDepth = 1024 };

    [Theory]
    [InlineData(62)]
    [InlineData(63)]
    [InlineData(64)]
    public async Task WorkspaceMetadata_AtTheLimit_RoundTrips_AndLists(int depth)
    {
        using var harness = new RoundTripHarness();

        foreach (var client in new[] { harness.Http, harness.InProcess })
        {
            var created = await client.GetOrCreateWorkspaceAsync("w", Nested(depth));
            Depth(created.Metadata).ShouldBe(depth);
        }

        var http = await harness.Http.ListWorkspacesAsync(null, new PageRequest());
        var inProcess = await harness.InProcess.ListWorkspacesAsync(null, new PageRequest());
        Json(http).ShouldBe(Json(inProcess));
        Depth(http.Items.Single().Metadata).ShouldBe(depth);
    }

    [Fact]
    public async Task MessageMetadata_AtTheLimit_IsSerialized_AndReadBack()
    {
        using var harness = new RoundTripHarness();
        var results = new List<string>();

        foreach (var client in new[] { harness.Http, harness.InProcess })
        {
            await client.GetOrCreateWorkspaceAsync("w");
            await client.GetOrCreateSessionAsync("w", "s");
            var created = await client.CreateMessagesAsync("w", "s", [new MessageCreate("deep", "alice", Nested(64))]);
            var listed = await client.ListMessagesAsync("w", "s", null, new PageRequest());
            var updated = await client.UpdatePeerAsync("w", "alice", Nested(64), Nested(64));
            var peers = await client.ListPeersAsync("w", null, null, new PageRequest());

            Depth(created.Single().Metadata).ShouldBe(64);
            results.Add(Json(new { listed, updated, peers }).Replace(created.Single().Id, "<id>", StringComparison.Ordinal));
        }

        results[0].ShouldBe(results[1]);
    }

    [Fact]
    public async Task Metadata_OverTheLimit_IsRejectedAlike_AndNothingIsSent()
    {
        using var harness = new RoundTripHarness();
        var failures = new List<Exception>();
        foreach (var client in new[] { harness.Http, harness.InProcess })
        {
            await client.GetOrCreateWorkspaceAsync("w");
            failures.Add(await Should.ThrowAsync<NachosValidationException>(() => client.UpdateWorkspaceAsync("w", Nested(65))));
        }

        failures[0].Message.ShouldBe(failures[1].Message);
        harness.Wire.Requests.ShouldBe(["POST /v3/workspaces"]);
    }

    [Fact]
    public async Task Server_RejectsMetadataOverTheLimit_WithA422_ThatMapsLikeInProcess()
    {
        // The client refuses depth 65 before sending, so the server's own check is reached with a raw body, and its
        // response goes through the mapper the client uses for every non-success status.
        using var harness = new RoundTripHarness();
        await harness.InProcess.GetOrCreateWorkspaceAsync("w");
        var inProcess = await Should.ThrowAsync<NachosException>(() => harness.InProcess.UpdateWorkspaceAsync("w", Nested(65)));

        using var server = new NachosApiFactory();
        using var raw = server.CreateClient();
        (await raw.PostAsync("/v3/workspaces", Body(new JsonObject { ["id"] = "w" }))).EnsureSuccessStatusCode();
        using var response = await raw.PutAsync("/v3/workspaces/w", Body(new JsonObject { ["metadata"] = Nested(65) }));
        var text = await response.Content.ReadAsStringAsync();

        ((int)response.StatusCode).ShouldBe(422, text);
        var mapped = ErrorMapper.Map(response, text, "PUT /v3/workspaces/{workspace_id}", secret: null, TimeProvider.System);
        mapped.GetType().ShouldBe(inProcess.GetType());
        mapped.Message.ShouldBe(inProcess.Message);
    }

    [Fact]
    public async Task ResponseDepth_StaysBounded()
    {
        // A hostile or broken server cannot make the client parse arbitrarily deep JSON.
        var json = new JsonObject { ["id"] = "w", ["metadata"] = Nested(WireJson.MaxDepth), ["created_at"] = "2026-10-08T12:00:00Z" }
            .ToJsonString(Compare);
        var stub = new StubHandler((_, _) => StubHandler.Json(System.Net.HttpStatusCode.OK, json));
        var client = new NachosHttpClient(new HttpClient(stub), new NachosClientOptions { BaseAddress = new Uri("https://nachos.test/") });

        await Should.ThrowAsync<JsonException>(() => client.GetOrCreateWorkspaceAsync("w"));
        WireJson.MaxDepth.ShouldBe(StrictJsonData.DefaultMaxDepth + WireJson.EnvelopeAllowance);
        WireJson.EnvelopeAllowance.ShouldBeInRange(3, 16);
    }

    private static StringContent Body(JsonObject body) =>
        new(body.ToJsonString(Compare), System.Text.Encoding.UTF8, "application/json");

    private static JsonObject Nested(int depth)
    {
        var root = new JsonObject();
        var current = root;
        for (var level = 1; level < depth; level++)
        {
            var next = new JsonObject();
            current["k"] = next;
            current = next;
        }

        current["leaf"] = $"depth-{depth}";
        return root;
    }

    private static int Depth(JsonNode? node) => node switch
    {
        JsonObject obj => 1 + obj.Select(p => Depth(p.Value)).DefaultIfEmpty(0).Max(),
        JsonArray arr => 1 + arr.Select(Depth).DefaultIfEmpty(0).Max(),
        _ => 0,
    };

    private static string Json(object value) => JsonSerializer.Serialize(value, Compare);
}
