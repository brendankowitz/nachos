using System.Net;
using System.Text.Json;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Shouldly;

namespace Nachos.Client.Tests;

/// <summary>
/// A success body whose root or entries have the wrong JSON shape is a protocol error: it throws
/// <see cref="JsonException"/>, like any other malformed success body, and never yields a null or fabricated entity.
/// </summary>
public sealed class MalformedSuccessTests
{
    private const string WorkspaceJson = """{"id":"w1","created_at":"2026-10-08T12:00:00Z"}""";
    private const string PeerJson = """{"id":"alice","workspace_id":"w1","created_at":"2026-10-08T12:00:00Z"}""";
    private const string SessionJson = """{"id":"s1","is_active":true,"workspace_id":"w1","created_at":"2026-10-08T12:00:00Z"}""";
    private const string MessageJson =
        """{"id":"m1","content":"hi","peer_id":"alice","session_id":"s1","created_at":"2026-10-08T12:00:00Z","workspace_id":"w1","token_count":1}""";

    private static readonly Dictionary<string, (Func<INachosClient, Task> Call, string Item)> PageRoutes = new()
    {
        ["ListWorkspaces"] = (c => c.ListWorkspacesAsync(null, new PageRequest()), WorkspaceJson),
        ["ListPeers"] = (c => c.ListPeersAsync("w1", null, null, new PageRequest()), PeerJson),
        ["ListPeerSessions"] = (c => c.ListPeerSessionsAsync("w1", "alice", null, new PageRequest()), SessionJson),
        ["ListSessions"] = (c => c.ListSessionsAsync("w1", null, new PageRequest()), SessionJson),
        ["ListSessionPeers"] = (c => c.ListSessionPeersAsync("w1", "s1", new PageRequest()), PeerJson),
        ["ListMessages"] = (c => c.ListMessagesAsync("w1", "s1", null, new PageRequest()), MessageJson),
    };

    private static readonly Dictionary<string, Func<INachosClient, Task>> EntityRoutes = new()
    {
        ["GetOrCreateWorkspace"] = c => c.GetOrCreateWorkspaceAsync("w1"),
        ["UpdateWorkspace"] = c => c.UpdateWorkspaceAsync("w1"),
        ["GetOrCreatePeer"] = c => c.GetOrCreatePeerAsync("w1", "alice"),
        ["UpdatePeer"] = c => c.UpdatePeerAsync("w1", "alice"),
        ["GetOrCreateSession"] = c => c.GetOrCreateSessionAsync("w1", "s1"),
        ["UpdateSession"] = c => c.UpdateSessionAsync("w1", "s1"),
        ["AddSessionPeers"] = c => c.AddSessionPeersAsync("w1", "s1", new Dictionary<string, SessionPeerConfig>()),
        ["SetSessionPeers"] = c => c.SetSessionPeersAsync("w1", "s1", new Dictionary<string, SessionPeerConfig>()),
        ["RemoveSessionPeers"] = c => c.RemoveSessionPeersAsync("w1", "s1", ["alice"]),
        ["GetSessionPeerConfig"] = c => c.GetSessionPeerConfigAsync("w1", "s1", "alice"),
        ["GetMessage"] = c => c.GetMessageAsync("w1", "s1", "m1"),
        ["UpdateMessage"] = c => c.UpdateMessageAsync("w1", "s1", "m1", null),
        ["CreateKey"] = c => c.CreateKeyAsync("w1"),
    };

    /// <summary>{item} is replaced by a valid entity of the route's type.</summary>
    private static readonly string[] MalformedPages =
    [
        "[]",
        "[null]",
        "true",
        "1",
        "\"page\"",
        "null",
        """{"items":[null],"total":1,"page":1,"size":50,"pages":1}""",
        """{"items":[{item},null],"total":2,"page":1,"size":50,"pages":1}""",
        """{"items":[1],"total":1,"page":1,"size":50,"pages":1}""",
        """{"items":["x"],"total":1,"page":1,"size":50,"pages":1}""",
        """{"items":[[]],"total":1,"page":1,"size":50,"pages":1}""",
        """{"items":null,"total":0,"page":1,"size":50,"pages":0}""",
        """{"items":{},"total":0,"page":1,"size":50,"pages":0}""",
        """{"total":0,"page":1,"size":50,"pages":0}""",
    ];

    private static readonly string[] MalformedMessageBatches =
    [
        "[null]",
        $"[{MessageJson},null]",
        "[1]",
        "[\"m\"]",
        "[[]]",
        "{}",
        """{"items":[]}""",
        "null",
        "true",
    ];

    private static readonly string[] MalformedEntities = ["[]", "[null]", "null", "true", "1", "\"entity\""];

    public static TheoryData<string, int> PageCases()
    {
        var data = new TheoryData<string, int>();
        foreach (var route in PageRoutes.Keys)
        {
            for (var i = 0; i < MalformedPages.Length; i++)
            {
                data.Add(route, i);
            }
        }

        return data;
    }

    public static TheoryData<string, int> EntityCases()
    {
        var data = new TheoryData<string, int>();
        foreach (var route in EntityRoutes.Keys)
        {
            for (var i = 0; i < MalformedEntities.Length; i++)
            {
                data.Add(route, i);
            }
        }

        return data;
    }

    public static TheoryData<int> MessageBatchCases() => [.. Enumerable.Range(0, MalformedMessageBatches.Length)];

    [Theory]
    [MemberData(nameof(PageCases))]
    public async Task MalformedPage_ThrowsJsonException(string route, int bodyIndex)
    {
        var (call, item) = PageRoutes[route];
        var body = MalformedPages[bodyIndex].Replace("{item}", item, StringComparison.Ordinal);

        await ShouldThrowJson(call, HttpStatusCode.OK, body);
    }

    [Theory]
    [MemberData(nameof(MessageBatchCases))]
    public async Task MalformedMessageBatch_ThrowsJsonException(int bodyIndex)
    {
        await ShouldThrowJson(
            c => c.CreateMessagesAsync("w1", "s1", [new MessageCreate("hi", "alice")]),
            HttpStatusCode.Created,
            MalformedMessageBatches[bodyIndex]);
    }

    [Theory]
    [MemberData(nameof(EntityCases))]
    public async Task MalformedEntityRoot_ThrowsJsonException(string route, int bodyIndex)
    {
        await ShouldThrowJson(EntityRoutes[route], HttpStatusCode.OK, MalformedEntities[bodyIndex]);
    }

    [Fact]
    public async Task WellFormedPageAndBatch_StillRead()
    {
        foreach (var (route, (call, item)) in PageRoutes)
        {
            var stub = new StubHandler((_, _) => StubHandler.Json(
                HttpStatusCode.OK, $$"""{"items":[{{item}},{{item}}],"total":2,"page":1,"size":50,"pages":1}"""));
            await call(Client(stub));
            stub.Requests.Count.ShouldBe(1, route);
        }

        var batchStub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.Created, $"[{MessageJson},{MessageJson}]"));
        var messages = await Client(batchStub).CreateMessagesAsync("w1", "s1", [new MessageCreate("hi", "alice")]);
        messages.Count.ShouldBe(2);
        messages.ShouldAllBe(m => m != null && m.Metadata != null);
    }

    private static async Task ShouldThrowJson(Func<INachosClient, Task> call, HttpStatusCode status, string body)
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(status, body));

        var ex = await Should.ThrowAsync<Exception>(() => call(Client(stub)));

        ex.ShouldBeAssignableTo<JsonException>($"body {body} raised {ex.GetType().Name}: {ex.Message}");
    }

    private static NachosHttpClient Client(StubHandler stub) =>
        new(new HttpClient(stub), new NachosClientOptions { BaseAddress = new Uri("https://nachos.test/") });
}
