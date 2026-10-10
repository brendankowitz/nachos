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

    /// <summary>Where an entity is read: alone, as a page item, or as a batch entry. {item} marks the entity.</summary>
    private static readonly Dictionary<string, (Func<INachosClient, Task> Call, string Entity, string Envelope, bool HasConfiguration)> EntityPlacements = new()
    {
        ["GetOrCreateWorkspace"] = (c => c.GetOrCreateWorkspaceAsync("w1"), WorkspaceJson, "{item}", true),
        ["GetOrCreatePeer"] = (c => c.GetOrCreatePeerAsync("w1", "alice"), PeerJson, "{item}", true),
        ["UpdateSession"] = (c => c.UpdateSessionAsync("w1", "s1"), SessionJson, "{item}", true),
        ["GetMessage"] = (c => c.GetMessageAsync("w1", "s1", "m1"), MessageJson, "{item}", false),
        ["ListPeers.items"] = (c => c.ListPeersAsync("w1", null, null, new PageRequest()), PeerJson, """{"items":[{item}],"total":1,"page":1,"size":50,"pages":1}""", true),
        ["ListMessages.items"] = (c => c.ListMessagesAsync("w1", "s1", null, new PageRequest()), MessageJson, """{"items":[{item}],"total":1,"page":1,"size":50,"pages":1}""", false),
        ["CreateMessages.entry"] = (c => c.CreateMessagesAsync("w1", "s1", [new MessageCreate("hi", "alice")]), MessageJson, "[{item}]", false),
    };

    /// <summary>A member replaced by a wrong-typed value, or removed when the value is <c>null</c>.</summary>
    private static readonly (string Member, string? Value)[] WrongMembers =
    [
        ("metadata", "\"x\""),
        ("metadata", "[]"),
        ("metadata", "5"),
        ("configuration", "5"),
        ("configuration", "\"x\""),
        ("id", "7"),
        ("id", "null"),
        ("id", "{}"),
        ("id", null),
        ("created_at", "\"not a date\""),
    ];

    public static TheoryData<string, int> WrongMemberCases()
    {
        var data = new TheoryData<string, int>();
        foreach (var (placement, (_, _, _, hasConfiguration)) in EntityPlacements)
        {
            for (var i = 0; i < WrongMembers.Length; i++)
            {
                if (hasConfiguration || WrongMembers[i].Member != "configuration")
                {
                    data.Add(placement, i);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(WrongMemberCases))]
    public async Task WrongTypedOrMissingEntityMember_ThrowsJsonException(string placement, int memberIndex)
    {
        var (call, entity, envelope, _) = EntityPlacements[placement];
        var (member, value) = WrongMembers[memberIndex];
        var node = System.Text.Json.Nodes.JsonNode.Parse(entity)!.AsObject();
        node.Remove(member);
        if (value is not null)
        {
            node[member] = System.Text.Json.Nodes.JsonNode.Parse(value);
        }

        await ShouldThrowJson(call, HttpStatusCode.OK, envelope.Replace("{item}", node.ToJsonString(), StringComparison.Ordinal));
    }

    /// <summary>The page envelope's own members typed wrong or missing.</summary>
    [Theory]
    [InlineData("total", "\"1\"")]
    [InlineData("total", "null")]
    [InlineData("total", "1.5")]
    [InlineData("total", null)]
    [InlineData("page", "\"1\"")]
    [InlineData("page", "null")]
    [InlineData("page", "1.5")]
    [InlineData("page", null)]
    [InlineData("size", "\"50\"")]
    [InlineData("pages", "true")]
    public async Task WrongTypedOrMissingPageMember_ThrowsJsonException(string member, string? value)
    {
        foreach (var (call, item) in PageRoutes.Values)
        {
            var page = System.Text.Json.Nodes.JsonNode.Parse(
                $$"""{"items":[{{item}}],"total":1,"page":1,"size":50,"pages":1}""")!.AsObject();
            page.Remove(member);
            if (value is not null)
            {
                page[member] = System.Text.Json.Nodes.JsonNode.Parse(value);
            }

            await ShouldThrowJson(call, HttpStatusCode.OK, page.ToJsonString());
        }
    }

    /// <summary>
    /// An empty body, or an empty object where an entity, page or batch is expected. A session peer config is the
    /// exception: every member is optional, so <c>{}</c> is a valid one.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    public async Task EmptyBodyOrEmptyObject_ThrowsJsonException(string body)
    {
        var calls = PageRoutes.Values.Select(r => r.Call)
            .Concat(EntityRoutes.Where(r => r.Key != "GetSessionPeerConfig" || body != "{}").Select(r => r.Value))
            .Append(c => c.CreateMessagesAsync("w1", "s1", [new MessageCreate("hi", "alice")]));
        foreach (var call in calls)
        {
            await ShouldThrowJson(call, HttpStatusCode.OK, body);
        }
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

    /// <summary>A JWT-shaped canary, long enough that a 15-character piece of it is recognisable.</summary>
    private const string Token = "eyJhbGciOiJQUzI1NiJ9.CANARY-DUPLICATE-PROPERTY-TOKEN-7e3a.sig";

    private const string ApiKey = "nk-CANARY-DUPLICATE-PROPERTY-KEY-4c1f";

    public static TheoryData<string, string> DuplicateSecretPropertyCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var auth in new[] { "credential", "api key" })
        {
            data.Add(auth, "top level");
            data.Add(auth, "nested in metadata");
            data.Add(auth, "nested in a page item's metadata");
            data.Add(auth, "nested in a batch entry's metadata");
        }

        return data;
    }

    /// <summary>
    /// A 2xx body with a duplicate property whose name is the bearer value: the parser's own text would carry that name
    /// ("Duplicate property 'eyJ…'"), raised where no transport sanitizing runs. The protocol error is still a
    /// <see cref="JsonException"/>, with fixed text.
    /// </summary>
    [Theory]
    [MemberData(nameof(DuplicateSecretPropertyCases))]
    public async Task DuplicatePropertyNamedAfterTheBearer_ThrowsJsonException_WithFixedText(string auth, string placement)
    {
        var secret = auth == "credential" ? Token : ApiKey;
        var duplicate = $$"""{"{{secret}}":1,"{{secret}}":2}""";
        var (body, call) = placement switch
        {
            "top level" => (duplicate, (Func<INachosClient, Task>)(c => c.GetMessageAsync("w1", "s1", "m1"))),
            "nested in metadata" => (MessageJson[..^1] + $$""","metadata":{{duplicate}}}""", c => c.GetMessageAsync("w1", "s1", "m1")),
            "nested in a page item's metadata" => (
                $$"""{"items":[{{MessageJson[..^1]}},"metadata":{{duplicate}}}],"total":1,"page":1,"size":50,"pages":1}""",
                c => c.ListMessagesAsync("w1", "s1", null, new PageRequest())),
            _ => ($"[{MessageJson[..^1]},\"metadata\":{duplicate}}}]", c => c.CreateMessagesAsync("w1", "s1", [new MessageCreate("hi", "alice")])),
        };
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, body));
        var options = new NachosClientOptions { BaseAddress = new Uri("https://nachos.test/"), ApiKey = ApiKey };
        if (auth == "credential")
        {
            options.Credential = new StaticCredential(Token);
            options.Scopes = ["api://nachos/.default"];
        }

        var ex = await Should.ThrowAsync<JsonException>(() => call(new NachosHttpClient(new HttpClient(stub), options)));

        stub.Requests.Single().Authorization.ShouldBe("Bearer " + secret);
        ex.Message.ShouldBe(
            "The response body is not valid JSON (malformed, a repeated property name, or nested past 80 levels). The parser's own description is withheld because it can repeat what the server sent.");
        ex.InnerException.ShouldBeNull();
        SecretScan.FindLeak(ex.ToString(), secret, window: 12).ShouldBeNull();
    }

    [Theory]
    [InlineData("""{"a":1,"a":2}""")]
    [InlineData("""{"a":1""")]
    [InlineData("not json")]
    public void ParseError_HasFixedText_WithoutTheBodyOrAnInnerException(string body)
    {
        var ex = Should.Throw<JsonException>(() => WireJson.Parse(body));

        ex.Message.ShouldBe(WireJson.InvalidBodyMessage);
        ex.InnerException.ShouldBeNull();
        ex.ToString().ShouldNotContain("'a'");
        ex.ToString().ShouldNotContain("not json");
    }

    private static async Task ShouldThrowJson(Func<INachosClient, Task> call, HttpStatusCode status, string body)
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(status, body));

        var ex = await Should.ThrowAsync<Exception>(() => call(Client(stub)));

        ex.ShouldBeAssignableTo<JsonException>($"body {body} raised {ex.GetType().Name}: {ex.Message}");
    }

    private static NachosHttpClient Client(StubHandler stub) =>
        new(new HttpClient(stub), new NachosClientOptions { BaseAddress = new Uri("https://nachos.test/") });

    private sealed class StaticCredential(string token) : Azure.Core.TokenCredential
    {
        public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(token, DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
