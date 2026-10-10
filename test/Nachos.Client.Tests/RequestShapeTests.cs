using System.Net;
using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Shouldly;

namespace Nachos.Client.Tests;

/// <summary>
/// One <c>{Operation}_Shape</c> test per <see cref="INachosClient"/> operation: method, route template, path, query
/// and JSON body sent, and the manifest-shaped response deserialized into the Abstractions type.
/// </summary>
public sealed class RequestShapeTests
{
    private const string W = "/v3/workspaces/{workspace_id}";
    private const string P = W + "/peers/{peer_id}";
    private const string S = W + "/sessions/{session_id}";
    private const string M = S + "/messages";

    private const string CreatedAt = "2026-10-08T12:00:00Z";

    private const string WorkspaceJson =
        """{"id":"w1","metadata":{"tier":"gold"},"configuration":{"reasoning":{"enabled":true}},"created_at":"2026-10-08T12:00:00Z"}""";

    private const string PeerJson =
        """{"id":"alice","workspace_id":"w1","created_at":"2026-10-08T12:00:00Z","metadata":{"role":"user"},"configuration":{"observe_me":true}}""";

    private const string SessionJson =
        """{"id":"s1","is_active":true,"workspace_id":"w1","metadata":{"topic":"x"},"configuration":{},"created_at":"2026-10-08T12:00:00Z"}""";

    private const string MessageJson =
        """{"id":"m1","content":"hello","peer_id":"alice","session_id":"s1","metadata":{"k":"v"},"created_at":"2026-10-08T12:00:00Z","workspace_id":"w1","token_count":3}""";

    private static readonly NachosClientOptions Options = new() { BaseAddress = new Uri("https://nachos.test/"), ApiKey = "nk-test" };

    [Fact]
    public void EveryClientOperation_HasAShapeTest()
    {
        var tests = typeof(RequestShapeTests).GetMethods().Select(m => m.Name).ToHashSet(StringComparer.Ordinal);

        var missing = typeof(INachosClient).GetMethods()
            .Select(m => m.Name + "_Shape")
            .Where(name => !tests.Contains(name))
            .ToArray();

        missing.ShouldBeEmpty();
        typeof(INachosClient).GetMethods().Length.ShouldBe(22);
    }

    [Fact]
    public async Task GetOrCreateWorkspaceAsync_Shape()
    {
        var (workspace, request) = await Send(
            c => c.GetOrCreateWorkspaceAsync(
                "w1", new JsonObject { ["tier"] = "gold" }, new WorkspaceConfiguration(Reasoning: new(Enabled: true))),
            HttpStatusCode.OK,
            WorkspaceJson);

        AssertRequest(request, "POST", "/v3/workspaces", "/v3/workspaces",
            """{"id":"w1","metadata":{"tier":"gold"},"configuration":{"reasoning":{"enabled":true}}}""");
        workspace.Id.ShouldBe("w1");
        workspace.Metadata["tier"]!.GetValue<string>().ShouldBe("gold");
        workspace.Configuration["reasoning"]!["enabled"]!.GetValue<bool>().ShouldBeTrue();
        workspace.CreatedAt.ShouldBe(DateTimeOffset.Parse(CreatedAt, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task GetOrCreateWorkspaceAsync_NullOptionals_AreOmitted()
    {
        var (_, request) = await Send(c => c.GetOrCreateWorkspaceAsync("w1"), HttpStatusCode.OK, WorkspaceJson);

        AssertRequest(request, "POST", "/v3/workspaces", "/v3/workspaces", """{"id":"w1"}""");
    }

    [Fact]
    public async Task ListWorkspacesAsync_Shape()
    {
        var (page, request) = await Send(
            c => c.ListWorkspacesAsync(new JsonObject { ["metadata"] = new JsonObject { ["tier"] = "gold" } }, new PageRequest(2, 10, Reverse: true)),
            HttpStatusCode.OK,
            PageOf(WorkspaceJson, page: 2, size: 10));

        AssertRequest(request, "POST", "/v3/workspaces/list", "/v3/workspaces/list?page=2&size=10&reverse=true",
            """{"filters":{"metadata":{"tier":"gold"}}}""");
        AssertPage(page, page: 2, size: 10);
        page.Items.Single().Id.ShouldBe("w1");
    }

    [Fact]
    public async Task UpdateWorkspaceAsync_Shape()
    {
        var (workspace, request) = await Send(
            c => c.UpdateWorkspaceAsync("w1", new JsonObject { ["a"] = 1 }),
            HttpStatusCode.OK,
            WorkspaceJson);

        AssertRequest(request, "PUT", W, "/v3/workspaces/w1", """{"metadata":{"a":1}}""");
        workspace.Id.ShouldBe("w1");
    }

    [Fact]
    public async Task GetOrCreatePeerAsync_Shape()
    {
        var (peer, request) = await Send(
            c => c.GetOrCreatePeerAsync("w1", "alice", configuration: new JsonObject { ["observe_me"] = true }),
            HttpStatusCode.OK,
            PeerJson);

        AssertRequest(request, "POST", W + "/peers", "/v3/workspaces/w1/peers",
            """{"id":"alice","configuration":{"observe_me":true}}""");
        peer.Id.ShouldBe("alice");
        peer.WorkspaceId.ShouldBe("w1");
        peer.Metadata["role"]!.GetValue<string>().ShouldBe("user");
        peer.Configuration["observe_me"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task ListPeersAsync_Shape()
    {
        var (page, request) = await Send(
            c => c.ListPeersAsync("w1", PeerKind.Scope, null, new PageRequest()),
            HttpStatusCode.OK,
            PageOf(PeerJson, page: 1, size: 50));

        AssertRequest(request, "POST", W + "/peers/list", "/v3/workspaces/w1/peers/list?page=1&size=50&reverse=false",
            """{"kind":"scope"}""");
        AssertPage(page, page: 1, size: 50);
        page.Items.Single().Id.ShouldBe("alice");
    }

    [Theory]
    [InlineData(null, "{}")]
    [InlineData(PeerKind.Regular, "{}")]
    [InlineData(PeerKind.Scope, """{"kind":"scope"}""")]
    [InlineData(PeerKind.All, """{"kind":"all","filters":{"id":"alice"}}""")]
    public async Task ListPeersAsync_KindOnTheWire(PeerKind? kind, string expectedBody)
    {
        var filters = kind == PeerKind.All ? new JsonObject { ["id"] = "alice" } : null;
        var (_, request) = await Send(
            c => c.ListPeersAsync("w1", kind, filters, new PageRequest()),
            HttpStatusCode.OK,
            PageOf(PeerJson, page: 1, size: 50));

        AssertBody(request, expectedBody);
    }

    [Fact]
    public async Task UpdatePeerAsync_Shape()
    {
        var (peer, request) = await Send(
            c => c.UpdatePeerAsync("w1", "alice", new JsonObject { ["x"] = 1 }, new JsonObject { ["observe_me"] = false }),
            HttpStatusCode.OK,
            PeerJson);

        AssertRequest(request, "PUT", P, "/v3/workspaces/w1/peers/alice",
            """{"metadata":{"x":1},"configuration":{"observe_me":false}}""");
        peer.Id.ShouldBe("alice");
    }

    [Fact]
    public async Task ListPeerSessionsAsync_Shape()
    {
        var (page, request) = await Send(
            c => c.ListPeerSessionsAsync("w1", "alice", new JsonObject { ["is_active"] = true }, new PageRequest(1, 5)),
            HttpStatusCode.OK,
            PageOf(SessionJson, page: 1, size: 5));

        AssertRequest(request, "POST", P + "/sessions", "/v3/workspaces/w1/peers/alice/sessions?page=1&size=5&reverse=false",
            """{"filters":{"is_active":true}}""");
        AssertPage(page, page: 1, size: 5);
        page.Items.Single().IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task GetOrCreateSessionAsync_Shape()
    {
        var peers = new Dictionary<string, SessionPeerConfig>
        {
            ["alice"] = new(ObserveMe: true),
            ["bob"] = new(ObserveOthers: false),
        };
        var (session, request) = await Send(
            c => c.GetOrCreateSessionAsync(
                "w1", "s1", new JsonObject { ["topic"] = "x" }, new SessionConfiguration(Summary: new(Enabled: false)), peers),
            HttpStatusCode.OK,
            SessionJson);

        AssertRequest(request, "POST", W + "/sessions", "/v3/workspaces/w1/sessions",
            """{"id":"s1","metadata":{"topic":"x"},"configuration":{"summary":{"enabled":false}},"peers":{"alice":{"observe_me":true},"bob":{"observe_others":false}}}""");
        session.Id.ShouldBe("s1");
        session.IsActive.ShouldBeTrue();
        session.WorkspaceId.ShouldBe("w1");
        session.Metadata["topic"]!.GetValue<string>().ShouldBe("x");
    }

    [Fact]
    public async Task ListSessionsAsync_Shape()
    {
        var (page, request) = await Send(
            c => c.ListSessionsAsync("w1", null, new PageRequest()),
            HttpStatusCode.OK,
            PageOf(SessionJson, page: 1, size: 50));

        AssertRequest(request, "POST", W + "/sessions/list", "/v3/workspaces/w1/sessions/list?page=1&size=50&reverse=false", "{}");
        AssertPage(page, page: 1, size: 50);
    }

    [Fact]
    public async Task UpdateSessionAsync_Shape()
    {
        var (session, request) = await Send(
            c => c.UpdateSessionAsync("w1", "s1", configuration: new SessionConfiguration(CustomInstructions: "be brief")),
            HttpStatusCode.OK,
            SessionJson);

        AssertRequest(request, "PUT", S, "/v3/workspaces/w1/sessions/s1", """{"configuration":{"custom_instructions":"be brief"}}""");
        session.Id.ShouldBe("s1");
    }

    [Fact]
    public async Task AddSessionPeersAsync_Shape()
    {
        var (session, request) = await Send(
            c => c.AddSessionPeersAsync("w1", "s1", new Dictionary<string, SessionPeerConfig> { ["alice"] = new(ObserveMe: true) }),
            HttpStatusCode.OK,
            SessionJson);

        AssertRequest(request, "POST", S + "/peers", "/v3/workspaces/w1/sessions/s1/peers", """{"alice":{"observe_me":true}}""");
        session.Id.ShouldBe("s1");
    }

    [Fact]
    public async Task SetSessionPeersAsync_Shape()
    {
        var (session, request) = await Send(
            c => c.SetSessionPeersAsync("w1", "s1", new Dictionary<string, SessionPeerConfig> { ["bob"] = new() }),
            HttpStatusCode.OK,
            SessionJson);

        AssertRequest(request, "PUT", S + "/peers", "/v3/workspaces/w1/sessions/s1/peers", """{"bob":{}}""");
        session.Id.ShouldBe("s1");
    }

    [Fact]
    public async Task RemoveSessionPeersAsync_Shape()
    {
        var (session, request) = await Send(
            c => c.RemoveSessionPeersAsync("w1", "s1", ["alice", "bob"]),
            HttpStatusCode.OK,
            SessionJson);

        AssertRequest(request, "DELETE", S + "/peers", "/v3/workspaces/w1/sessions/s1/peers", """["alice","bob"]""");
        session.Id.ShouldBe("s1");
    }

    [Fact]
    public async Task ListSessionPeersAsync_Shape()
    {
        var (page, request) = await Send(
            c => c.ListSessionPeersAsync("w1", "s1", new PageRequest(3, 20, Reverse: true)),
            HttpStatusCode.OK,
            PageOf(PeerJson, page: 3, size: 20));

        // The route accepts only page and size, so Reverse is not sent.
        AssertRequest(request, "GET", S + "/peers", "/v3/workspaces/w1/sessions/s1/peers?page=3&size=20", null);
        AssertPage(page, page: 3, size: 20);
    }

    [Fact]
    public async Task GetSessionPeerConfigAsync_Shape()
    {
        var (config, request) = await Send(
            c => c.GetSessionPeerConfigAsync("w1", "s1", "alice"),
            HttpStatusCode.OK,
            """{"observe_me":true,"observe_others":false}""");

        AssertRequest(request, "GET", S + "/peers/{peer_id}/config", "/v3/workspaces/w1/sessions/s1/peers/alice/config", null);
        config.ShouldBe(new SessionPeerConfig(ObserveMe: true, ObserveOthers: false));
    }

    [Fact]
    public async Task SetSessionPeerConfigAsync_Shape()
    {
        var (_, request) = await Send(
            async c =>
            {
                await c.SetSessionPeerConfigAsync("w1", "s1", "alice", new SessionPeerConfig(ObserveMe: false, ObserveOthers: true));
                return 0;
            },
            HttpStatusCode.NoContent,
            null);

        AssertRequest(request, "PUT", S + "/peers/{peer_id}/config", "/v3/workspaces/w1/sessions/s1/peers/alice/config",
            """{"observe_me":false,"observe_others":true}""");
    }

    [Fact]
    public async Task CreateMessagesAsync_Shape()
    {
        IReadOnlyList<MessageCreate> batch =
        [
            new("hello", "alice", new JsonObject { ["k"] = "v" }, new MessageConfiguration(new ReasoningConfiguration(Enabled: false)),
                new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)),
            new(string.Empty, "bob"),
        ];
        var (messages, request) = await Send(
            c => c.CreateMessagesAsync("w1", "s1", batch),
            HttpStatusCode.Created,
            $"[{MessageJson},{MessageJson.Replace("\"m1\"", "\"m2\"", StringComparison.Ordinal)}]");

        AssertRequest(request, "POST", M, "/v3/workspaces/w1/sessions/s1/messages",
            """{"messages":[{"content":"hello","peer_id":"alice","metadata":{"k":"v"},"configuration":{"reasoning":{"enabled":false}},"created_at":"2026-01-02T03:04:05+00:00"},{"content":"","peer_id":"bob"}]}""");
        Guid.TryParse(request.IdempotencyKey, out _).ShouldBeTrue($"Idempotency-Key was '{request.IdempotencyKey}'");
        messages.Select(m => m.Id).ShouldBe(["m1", "m2"]);
        messages[0].TokenCount.ShouldBe(3);
        messages[0].Metadata["k"]!.GetValue<string>().ShouldBe("v");
    }

    [Fact]
    public async Task CreateMessagesAsync_CallerKey_IsSentVerbatim()
    {
        var (_, request) = await Send(
            c => c.CreateMessagesAsync("w1", "s1", [new MessageCreate("hi", "alice")], idempotencyKey: "caller-key-1"),
            HttpStatusCode.Created,
            "[]");

        request.IdempotencyKey.ShouldBe("caller-key-1");
    }

    [Fact]
    public async Task ListMessagesAsync_Shape()
    {
        var (page, request) = await Send(
            c => c.ListMessagesAsync("w1", "s1", new JsonObject { ["peer_id"] = "alice" }, new PageRequest(1, 100, Reverse: true)),
            HttpStatusCode.OK,
            PageOf(MessageJson, page: 1, size: 100));

        AssertRequest(request, "POST", M + "/list", "/v3/workspaces/w1/sessions/s1/messages/list?page=1&size=100&reverse=true",
            """{"filters":{"peer_id":"alice"}}""");
        AssertPage(page, page: 1, size: 100);
        page.Items.Single().Content.ShouldBe("hello");
    }

    [Fact]
    public async Task GetMessageAsync_Shape()
    {
        var (message, request) = await Send(c => c.GetMessageAsync("w1", "s1", "m1"), HttpStatusCode.OK, MessageJson);

        AssertRequest(request, "GET", M + "/{message_id}", "/v3/workspaces/w1/sessions/s1/messages/m1", null);
        message.ShouldSatisfyAllConditions(
            m => m.Id.ShouldBe("m1"),
            m => m.Content.ShouldBe("hello"),
            m => m.PeerId.ShouldBe("alice"),
            m => m.SessionId.ShouldBe("s1"),
            m => m.WorkspaceId.ShouldBe("w1"),
            m => m.TokenCount.ShouldBe(3));
    }

    [Fact]
    public async Task UpdateMessageAsync_Shape()
    {
        var (message, request) = await Send(
            c => c.UpdateMessageAsync("w1", "s1", "m1", new JsonObject { ["k"] = "v2" }),
            HttpStatusCode.OK,
            MessageJson);

        AssertRequest(request, "PUT", M + "/{message_id}", "/v3/workspaces/w1/sessions/s1/messages/m1", """{"metadata":{"k":"v2"}}""");
        message.Id.ShouldBe("m1");
    }

    [Fact]
    public async Task UpdateMessageAsync_NullMetadata_SendsEmptyBody()
    {
        var (_, request) = await Send(c => c.UpdateMessageAsync("w1", "s1", "m1", null), HttpStatusCode.OK, MessageJson);

        AssertBody(request, "{}");
    }

    [Fact]
    public async Task CreateKeyAsync_Shape()
    {
        var expires = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var (key, request) = await Send(
            c => c.CreateKeyAsync("w1", "alice", null, expires),
            HttpStatusCode.OK,
            """{"key":"eyJ.issued.key"}""");

        request.Method.ShouldBe(HttpMethod.Post);
        request.RouteTemplate.ShouldBe("/v3/keys");
        request.Uri.AbsolutePath.ShouldBe("/v3/keys");
        Query(request.Uri).ShouldBe(
        [
            ("workspace_id", "w1"),
            ("peer_id", "alice"),
            ("expires_at", "2027-01-01T00:00:00.0000000+00:00"),
        ]);
        request.Body.ShouldBeNull();
        key.Key.ShouldBe("eyJ.issued.key");
    }

    [Fact]
    public async Task AddGrantAsync_Shape()
    {
        var (_, request) = await Send(
            async c =>
            {
                await c.AddGrantAsync("oid-1", null, GrantRoles.Workspace);
                return 0;
            },
            HttpStatusCode.NoContent,
            null);

        AssertRequest(request, "POST", "/v3/admin/grants", "/v3/admin/grants", """{"object_id":"oid-1","role":"Nachos.Workspace"}""");
    }

    [Fact]
    public async Task AddGrantAsync_WithWorkspace()
    {
        var (_, request) = await Send(
            async c =>
            {
                await c.AddGrantAsync("oid-1", "w1", GrantRoles.Workspace);
                return 0;
            },
            HttpStatusCode.NoContent,
            null);

        AssertBody(request, """{"object_id":"oid-1","workspace_id":"w1","role":"Nachos.Workspace"}""");
    }

    [Fact]
    public async Task ApiKey_IsSentAsBearer()
    {
        var (_, request) = await Send(c => c.GetMessageAsync("w1", "s1", "m1"), HttpStatusCode.OK, MessageJson);

        request.Authorization.ShouldBe("Bearer nk-test");
    }

    [Fact]
    public async Task NoApiKey_SendsNoAuthorization()
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, MessageJson));
        var client = new NachosHttpClient(new HttpClient(stub), new NachosClientOptions { BaseAddress = Options.BaseAddress });

        await client.GetMessageAsync("w1", "s1", "m1");

        stub.Requests.Single().Authorization.ShouldBeNull();
    }

    [Theory]
    [InlineData("https://nachos.test/api")]
    [InlineData("https://nachos.test/api/")]
    public async Task BaseAddressPath_IsPreserved(string baseAddress)
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, MessageJson));
        var client = new NachosHttpClient(new HttpClient(stub), new NachosClientOptions { BaseAddress = new Uri(baseAddress) });

        await client.GetMessageAsync("w1", "s1", "m1");

        stub.Requests.Single().Uri.ToString().ShouldBe("https://nachos.test/api/v3/workspaces/w1/sessions/s1/messages/m1");
    }

    [Fact]
    public async Task PathSegments_AreEscaped()
    {
        var (_, request) = await Send(c => c.GetMessageAsync("w1", "s1", "../a b"), HttpStatusCode.OK, MessageJson);

        request.Uri.AbsolutePath.ShouldBe("/v3/workspaces/w1/sessions/s1/messages/..%2Fa%20b");
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("")]
    public async Task DotSegmentOrEmptyRouteValue_IsRejectedBeforeSending(string peerId)
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, PeerJson));
        var client = new NachosHttpClient(new HttpClient(stub), Options);

        // "PUT /v3/workspaces/w1/peers/.." would otherwise collapse to "PUT /v3/workspaces/w1" (a workspace update).
        var ex = await Should.ThrowAsync<ArgumentException>(() => client.UpdatePeerAsync("w1", peerId, new JsonObject()));

        ex.ParamName.ShouldBe("peer_id");
        stub.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("nk-secret\r\nX-Injected: 1")]
    [InlineData("nk secret")]
    [InlineData("")]
    public void InvalidApiKey_IsRejected_WithoutEchoingIt(string apiKey)
    {
        var ex = Should.Throw<ArgumentException>(() =>
            new NachosHttpClient(new HttpClient(), new NachosClientOptions { BaseAddress = Options.BaseAddress, ApiKey = apiKey }));

        if (apiKey.Length > 0)
        {
            ex.ToString().ShouldNotContain(apiKey);
        }
    }

    [Fact]
    public void RelativeBaseAddress_IsRejected()
    {
        Should.Throw<ArgumentException>(() =>
            new NachosHttpClient(new HttpClient(), new NachosClientOptions { BaseAddress = new Uri("/api", UriKind.Relative) }));
    }

    [Theory]
    [InlineData("ftp://nachos.test/")]
    [InlineData("file:///tmp/nachos")]
    public void NonHttpBaseAddress_IsRejected(string baseAddress)
    {
        var ex = Should.Throw<ArgumentException>(() =>
            new NachosHttpClient(new HttpClient(), new NachosClientOptions { BaseAddress = new Uri(baseAddress) }));

        ex.Message.ShouldNotContain(baseAddress);
    }

    [Fact]
    public async Task AbsentOptionalResponseFields_AreHandled()
    {
        var (workspace, _) = await Send(
            c => c.GetOrCreateWorkspaceAsync("w1"), HttpStatusCode.OK, """{"id":"w1","created_at":"2026-10-08T12:00:00Z"}""");
        workspace.Metadata.ShouldBeEmpty();
        workspace.Configuration.ShouldBeEmpty();

        var (session, _) = await Send(
            c => c.GetOrCreateSessionAsync("w1", "s1"),
            HttpStatusCode.OK,
            """{"id":"s1","is_active":false,"workspace_id":"w1","metadata":null,"created_at":"2026-10-08T12:00:00Z"}""");
        session.Metadata.ShouldBeEmpty();
        session.Configuration.ShouldBeEmpty();

        var (messages, _) = await Send(
            c => c.CreateMessagesAsync("w1", "s1", [new MessageCreate("hi", "alice")]),
            HttpStatusCode.Created,
            """[{"id":"m1","content":"hi","peer_id":"alice","session_id":"s1","created_at":"2026-10-08T12:00:00Z","workspace_id":"w1","token_count":1}]""");
        messages.Single().Metadata.ShouldBeEmpty();

        var (config, _) = await Send(c => c.GetSessionPeerConfigAsync("w1", "s1", "alice"), HttpStatusCode.OK, "{}");
        config.ShouldBe(new SessionPeerConfig());

        var (page, _) = await Send(
            c => c.ListPeersAsync("w1", null, null, new PageRequest()),
            HttpStatusCode.OK,
            """{"items":[{"id":"alice","workspace_id":"w1","created_at":"2026-10-08T12:00:00Z"}],"total":1,"page":1,"size":50,"pages":1}""");
        page.Items.Single().Metadata.ShouldBeEmpty();
    }

    [Fact]
    public async Task MissingRequiredResponseField_Throws()
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, """{"metadata":{},"created_at":"2026-10-08T12:00:00Z"}"""));
        var client = new NachosHttpClient(new HttpClient(stub), Options);

        await Should.ThrowAsync<System.Text.Json.JsonException>(() => client.GetOrCreateWorkspaceAsync("w1"));
    }

    [Theory]
    [InlineData("""{"id":"w1","id":"w2","created_at":"2026-10-08T12:00:00Z"}""")]
    [InlineData("""{"id":"w1","metadata":{"a":1,"a":2},"created_at":"2026-10-08T12:00:00Z"}""")]
    [InlineData("""{"id":"w1","created_at":"2026-10-08T12:00:00Z""")]
    public async Task MalformedOrDuplicateKeySuccessBody_ThrowsJsonException(string body)
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, body));
        var client = new NachosHttpClient(new HttpClient(stub), Options);

        var ex = await Should.ThrowAsync<Exception>(() => client.GetOrCreateWorkspaceAsync("w1"));

        ex.ShouldBeAssignableTo<System.Text.Json.JsonException>();
    }

    [Fact]
    public async Task DuplicateKeyPageItem_ThrowsJsonException()
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(
            HttpStatusCode.OK,
            """{"items":[{"id":"p","id":"q","workspace_id":"w1","created_at":"2026-10-08T12:00:00Z"}],"total":1,"page":1,"size":50,"pages":1}"""));
        var client = new NachosHttpClient(new HttpClient(stub), Options);

        var ex = await Should.ThrowAsync<Exception>(() => client.ListPeersAsync("w1", null, null, new PageRequest()));

        ex.ShouldBeAssignableTo<System.Text.Json.JsonException>();
    }

    [Fact]
    public async Task CancelledToken_IsHonoured()
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, MessageJson));
        var client = new NachosHttpClient(new HttpClient(stub), Options);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => client.GetMessageAsync("w1", "s1", "m1", cts.Token));
        stub.Requests.ShouldBeEmpty();
    }

    private static async Task<(T Result, RecordedRequest Request)> Send<T>(
        Func<INachosClient, Task<T>> call, HttpStatusCode status, string? responseJson)
    {
        var stub = new StubHandler((_, _) =>
            responseJson is null ? new HttpResponseMessage(status) : StubHandler.Json(status, responseJson));
        var client = new NachosHttpClient(new HttpClient(stub), Options);

        var result = await call(client);

        stub.Requests.Count.ShouldBe(1);
        return (result, stub.Requests[0]);
    }

    private static void AssertRequest(RecordedRequest request, string method, string template, string pathAndQuery, string? body)
    {
        request.Method.Method.ShouldBe(method);
        request.RouteTemplate.ShouldBe(template);
        WireManifest.Contains(request.Method, template).ShouldBeTrue($"{method} {template} is not a manifest route");
        request.Uri.GetLeftPart(UriPartial.Authority).ShouldBe("https://nachos.test");
        request.Uri.PathAndQuery.ShouldBe(pathAndQuery);
        if (body is null)
        {
            request.Body.ShouldBeNull();
        }
        else
        {
            AssertBody(request, body);
        }
    }

    private static void AssertBody(RecordedRequest request, string expected)
    {
        request.ContentTypeHeader.ShouldBe("application/json; charset=utf-8");
        JsonNode.DeepEquals(JsonNode.Parse(request.Body!), JsonNode.Parse(expected))
            .ShouldBeTrue($"expected {expected} but sent {request.Body}");
    }

    private static void AssertPage<T>(Page<T> actual, int page, int size)
    {
        actual.PageNumber.ShouldBe(page);
        actual.Size.ShouldBe(size);
        actual.Total.ShouldBe(1);
        actual.Pages.ShouldBe(1);
        actual.Items.Count.ShouldBe(1);
    }

    private static string PageOf(string itemJson, int page, int size) =>
        $$"""{"items":[{{itemJson}}],"total":1,"page":{{page}},"size":{{size}},"pages":1}""";

    private static (string, string)[] Query(Uri uri) =>
        uri.Query.TrimStart('?').Split('&')
            .Select(pair => pair.Split('=', 2))
            .Select(kv => (Uri.UnescapeDataString(kv[0]), Uri.UnescapeDataString(kv[1])))
            .ToArray();
}
