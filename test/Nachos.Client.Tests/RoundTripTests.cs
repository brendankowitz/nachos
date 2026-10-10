using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Shouldly;

namespace Nachos.Client.Tests;

/// <summary>
/// The headline check of the HTTP client (plan Task 12): every <see cref="INachosClient"/> operation, called through
/// <see cref="NachosHttpClient"/> (the <c>AddNachosClient</c> pipeline) against the real API host, gives the same
/// result or the same exception as the in-process client for the same call sequence. Each scenario runs once per
/// client on a fresh <see cref="RoundTripHarness"/>; every step is recorded as text and the two recordings must be
/// equal. Only server-generated message ids are normalized (to <c>&lt;message#n&gt;</c> by first appearance); clocks
/// are frozen at one instant on both sides, so every <c>created_at</c> is compared as it is. One outcome differs by
/// design and is asserted on each side instead of compared: an invalid body <c>id</c> on a create (see
/// <c>Steps.DoInvalidBodyId</c>).
/// </summary>
/// <remarks>
/// Coverage is enforced: <see cref="EveryOperation_HasARoundTrip"/> fails when an <see cref="INachosClient"/> method is
/// neither declared by a scenario nor listed in <see cref="AdminOperations"/>, and each scenario fails unless it actually
/// calls every operation it declares. The two admin operations (<c>POST /v3/keys</c>, <c>POST /v3/admin/grants</c>) need
/// authentication state the equality scenarios do not set up, so their HTTP outcomes are asserted on their own, each
/// with a single request.
/// </remarks>
public sealed class RoundTripTests
{
    private static readonly JsonSerializerOptions Compare = new() { MaxDepth = 1024 };

    private static readonly PageRequest FirstPage = new();

    /// <summary>Admin operations asserted individually below instead of compared with the in-process client.</summary>
    private static readonly string[] AdminOperations = [nameof(INachosClient.CreateKeyAsync), nameof(INachosClient.AddGrantAsync)];

    private static readonly Scenario[] Scenarios =
    [
        new(
            "workspace lifecycle",
            [nameof(INachosClient.GetOrCreateWorkspaceAsync), nameof(INachosClient.UpdateWorkspaceAsync)],
            async s =>
            {
                var configuration = new WorkspaceConfiguration(
                    new ReasoningConfiguration(true, "think"), new PeerCardConfiguration(Use: false), new SummaryConfiguration(true, 10, 40),
                    new DreamConfiguration(false), new DialecticConfiguration("short"), "be brief");
                await s.Do("create", c => c.GetOrCreateWorkspaceAsync("w", Obj("""{"tier":"gold","n":1.5,"tags":["a","b"],"nested":{"x":null}}"""), configuration));
                await s.Do("get-or-create is idempotent", c => c.GetOrCreateWorkspaceAsync("w", Obj("""{"tier":"other"}""")));
                await s.Do("create bare", c => c.GetOrCreateWorkspaceAsync("bare"));
                await s.Do("update metadata", c => c.UpdateWorkspaceAsync("w", Obj("""{"tier":"silver"}""")));
                await s.Do("update configuration only", c => c.UpdateWorkspaceAsync("w", configuration: new WorkspaceConfiguration(CustomInstructions: "longer")));
                await s.Do("update nothing", c => c.UpdateWorkspaceAsync("w"));
                await s.Do("update with empty metadata", c => c.UpdateWorkspaceAsync("w", new JsonObject()));
                await s.Do("update missing", c => c.UpdateWorkspaceAsync("missing", Obj("""{"a":1}""")));
                await s.DoInvalidBodyId("create invalid id", "bad id!", "string_pattern_mismatch", c => c.GetOrCreateWorkspaceAsync("bad id!"));
                await s.DoInvalidBodyId("create too-long id", new string('w', 513), "string_too_long", c => c.GetOrCreateWorkspaceAsync(new string('w', 513)));
                await s.DoInvalidBodyId("create empty id", "", "string_too_short", c => c.GetOrCreateWorkspaceAsync(""));
                await s.Do("update invalid id", c => c.UpdateWorkspaceAsync("bad id!", Obj("""{"a":1}""")));
                await s.Do("ids are case-sensitive", c => c.GetOrCreateWorkspaceAsync("W"));
                await s.Do("unicode metadata", c => c.UpdateWorkspaceAsync("W", Obj("""{"emoji":"😀","cjk":"漢字","rtl":"שלום","escaped":"\u0000\t\""}""")));
                await s.Do("only the valid ids exist", c => c.ListWorkspacesAsync(null, FirstPage));
            }),
        new(
            "workspace listing: paging envelope, reverse, filters",
            [nameof(INachosClient.GetOrCreateWorkspaceAsync), nameof(INachosClient.ListWorkspacesAsync)],
            async s =>
            {
                await s.Do("empty list", c => c.ListWorkspacesAsync(null, FirstPage));
                for (var i = 1; i <= 5; i++)
                {
                    var n = i;
                    await s.Do($"create w{n}", c => c.GetOrCreateWorkspaceAsync($"w{n}", Obj($$"""{"n":{{n}},"tag":"{{(n % 2 == 0 ? "even" : "odd")}}"}""")));
                }

                await s.Do("page 1 of 3", c => c.ListWorkspacesAsync(null, new PageRequest(1, 2)));
                await s.Do("page 3 partial", c => c.ListWorkspacesAsync(null, new PageRequest(3, 2)));
                await s.Do("page past the end", c => c.ListWorkspacesAsync(null, new PageRequest(10, 2)));
                await s.Do("reverse", c => c.ListWorkspacesAsync(null, new PageRequest(1, 3, Reverse: true)));
                await s.Do("max size", c => c.ListWorkspacesAsync(null, new PageRequest(1, PageRequest.MaxSize)));
                await s.Do("metadata filter", c => c.ListWorkspacesAsync(Obj("""{"metadata":{"tag":"even"}}"""), FirstPage));
                await s.Do("operator filter", c => c.ListWorkspacesAsync(Obj("""{"metadata":{"n":{"gte":3}}}"""), FirstPage));
                await s.Do("logical filter", c => c.ListWorkspacesAsync(Obj("""{"OR":[{"name":"w1"},{"name":{"in":["w4","w5"]}}]}"""), FirstPage));
                await s.Do("empty filter", c => c.ListWorkspacesAsync(new JsonObject(), FirstPage));
                await s.Do("unknown operator is 422", c => c.ListWorkspacesAsync(Obj("""{"name":{"regex":"w"}}"""), FirstPage));
                await s.Do("bad timestamp is 422", c => c.ListWorkspacesAsync(Obj("""{"created_at":{"gt":"not-a-date"}}"""), FirstPage));
            }),
        new(
            "peers: get-or-create, update, kinds, filters, paging",
            [nameof(INachosClient.GetOrCreatePeerAsync), nameof(INachosClient.UpdatePeerAsync), nameof(INachosClient.ListPeersAsync)],
            async s =>
            {
                await s.Do("peer in missing workspace", c => c.GetOrCreatePeerAsync("missing", "alice"));
                await s.Do("workspace", c => c.GetOrCreateWorkspaceAsync("w"));
                await s.Do("create alice", c => c.GetOrCreatePeerAsync("w", "alice", Obj("""{"role":"user"}"""), Obj("""{"observe_me":true}""")));
                await s.Do("get-or-create is idempotent", c => c.GetOrCreatePeerAsync("w", "alice", Obj("""{"role":"changed"}""")));
                await s.Do("create bob", c => c.GetOrCreatePeerAsync("w", "bob", Obj("""{"role":"agent"}""")));
                await s.Do("create Bob (case-sensitive)", c => c.GetOrCreatePeerAsync("w", "Bob"));
                await s.DoInvalidBodyId("invalid peer id", "bad id!", "string_pattern_mismatch", c => c.GetOrCreatePeerAsync("w", "bad id!"));
                await s.Do("update alice", c => c.UpdatePeerAsync("w", "alice", Obj("""{"role":"admin"}"""), Obj("""{"x":[1,2]}""")));
                await s.Do("update configuration only", c => c.UpdatePeerAsync("w", "bob", configuration: Obj("""{"y":true}""")));
                await s.Do("update missing peer", c => c.UpdatePeerAsync("w", "nobody", Obj("""{"a":1}""")));
                await s.Do("update in missing workspace", c => c.UpdatePeerAsync("missing", "alice", Obj("""{"a":1}""")));
                await s.Do("list regular (null kind)", c => c.ListPeersAsync("w", null, null, FirstPage));
                await s.Do("list regular", c => c.ListPeersAsync("w", PeerKind.Regular, null, FirstPage));
                await s.Do("list scope", c => c.ListPeersAsync("w", PeerKind.Scope, null, FirstPage));
                // M1 cannot create scope (internal) peers: the scope routes answer 501. So "scope" is empty and "all"
                // equals "regular" here; both still go over the wire with their kind and must match in-process.
                await s.Do("list all", c => c.ListPeersAsync("w", PeerKind.All, null, FirstPage));
                await s.Do("list filtered", c => c.ListPeersAsync("w", null, Obj("""{"metadata":{"role":"admin"}}"""), FirstPage));
                await s.Do("list paged reverse", c => c.ListPeersAsync("w", null, null, new PageRequest(2, 1, Reverse: true)));
                await s.Do("list bad filter", c => c.ListPeersAsync("w", null, Obj("""{"metadata":{"in":["a"]}}"""), FirstPage));
                await s.Do("list in missing workspace", c => c.ListPeersAsync("missing", null, null, FirstPage));
            }),
        new(
            "sessions: get-or-create with peers and configuration, update, list, peer sessions",
            [
                nameof(INachosClient.GetOrCreateSessionAsync), nameof(INachosClient.UpdateSessionAsync),
                nameof(INachosClient.ListSessionsAsync), nameof(INachosClient.ListPeerSessionsAsync),
            ],
            async s =>
            {
                await s.Do("session in missing workspace", c => c.GetOrCreateSessionAsync("missing", "s"));
                await s.Do("workspace", c => c.GetOrCreateWorkspaceAsync("w"));
                var configuration = new SessionConfiguration(Summary: new SummaryConfiguration(false), CustomInstructions: "session rules");
                await s.Do("create s1 with peers", c => c.GetOrCreateSessionAsync(
                    "w", "s1", Obj("""{"topic":"intro"}"""), configuration,
                    new Dictionary<string, SessionPeerConfig> { ["alice"] = new(ObserveMe: false), ["bob"] = new(ObserveOthers: true) }));
                await s.Do("get-or-create keeps the session but ensures carol", c => c.GetOrCreateSessionAsync(
                    "w", "s1", Obj("""{"topic":"ignored"}"""), peers: new Dictionary<string, SessionPeerConfig> { ["carol"] = new() }));
                await s.Do("create s2", c => c.GetOrCreateSessionAsync("w", "s2", Obj("""{"topic":"other"}""")));
                await s.Do("create s3 with alice", c => c.GetOrCreateSessionAsync("w", "s3", peers: new Dictionary<string, SessionPeerConfig> { ["alice"] = new() }));
                await s.DoInvalidBodyId("invalid session id", "bad id!", "string_pattern_mismatch", c => c.GetOrCreateSessionAsync("w", "bad id!"));
                await s.Do("update s1", c => c.UpdateSessionAsync("w", "s1", Obj("""{"topic":"updated"}"""), new SessionConfiguration(Dream: new DreamConfiguration(true))));
                await s.Do("update missing", c => c.UpdateSessionAsync("w", "nope", Obj("""{"a":1}""")));
                await s.Do("list", c => c.ListSessionsAsync("w", null, FirstPage));
                await s.Do("list paged", c => c.ListSessionsAsync("w", null, new PageRequest(2, 2)));
                await s.Do("list reverse", c => c.ListSessionsAsync("w", null, new PageRequest(1, 2, Reverse: true)));
                await s.Do("list filtered", c => c.ListSessionsAsync("w", Obj("""{"metadata":{"topic":"other"}}"""), FirstPage));
                await s.Do("list active", c => c.ListSessionsAsync("w", Obj("""{"is_active":true}"""), FirstPage));
                await s.Do("list bad filter", c => c.ListSessionsAsync("w", Obj("""{"is_active":"true"}"""), FirstPage));
                await s.Do("alice's sessions", c => c.ListPeerSessionsAsync("w", "alice", null, FirstPage));
                await s.Do("alice's sessions reversed", c => c.ListPeerSessionsAsync("w", "alice", null, new PageRequest(1, 1, Reverse: true)));
                await s.Do("alice's sessions filtered (s1 only, not s3)", c => c.ListPeerSessionsAsync("w", "alice", Obj("""{"metadata":{"topic":"updated"}}"""), FirstPage));
                await s.Do("carol's sessions", c => c.ListPeerSessionsAsync("w", "carol", null, FirstPage));
                await s.Do("sessions of a missing peer", c => c.ListPeerSessionsAsync("w", "nobody", null, FirstPage));
            }),
        new(
            "session membership and peer configuration",
            [
                nameof(INachosClient.GetOrCreateSessionAsync), nameof(INachosClient.AddSessionPeersAsync),
                nameof(INachosClient.SetSessionPeersAsync), nameof(INachosClient.RemoveSessionPeersAsync),
                nameof(INachosClient.ListSessionPeersAsync), nameof(INachosClient.GetSessionPeerConfigAsync),
                nameof(INachosClient.SetSessionPeerConfigAsync), nameof(INachosClient.ListPeerSessionsAsync),
            ],
            async s =>
            {
                await s.Do("workspace", c => c.GetOrCreateWorkspaceAsync("w"));
                await s.Do("session", c => c.GetOrCreateSessionAsync("w", "s"));
                await s.Do("add alice and bob", c => c.AddSessionPeersAsync("w", "s", new Dictionary<string, SessionPeerConfig>
                {
                    ["alice"] = new(ObserveMe: true, ObserveOthers: false),
                    ["bob"] = new(),
                }));
                await s.Do("members", c => c.ListSessionPeersAsync("w", "s", FirstPage));
                await s.Do("members paged (reverse ignored)", c => c.ListSessionPeersAsync("w", "s", new PageRequest(2, 1, Reverse: true)));
                await s.Do("alice's config", c => c.GetSessionPeerConfigAsync("w", "s", "alice"));
                await s.Do("bob's config", c => c.GetSessionPeerConfigAsync("w", "s", "bob"));
                await s.Do("set bob's config", c => c.SetSessionPeerConfigAsync("w", "s", "bob", new SessionPeerConfig(ObserveMe: false, ObserveOthers: true)));
                await s.Do("bob's config after set", c => c.GetSessionPeerConfigAsync("w", "s", "bob"));
                await s.Do("config of a non-member", c => c.GetSessionPeerConfigAsync("w", "s", "nobody"));
                await s.Do("set config of a non-member", c => c.SetSessionPeerConfigAsync("w", "s", "nobody", new SessionPeerConfig(true)));
                await s.Do("set exactly carol", c => c.SetSessionPeersAsync("w", "s", new Dictionary<string, SessionPeerConfig> { ["carol"] = new(ObserveMe: false) }));
                await s.Do("members after set", c => c.ListSessionPeersAsync("w", "s", FirstPage));
                await s.Do("alice's sessions after set", c => c.ListPeerSessionsAsync("w", "alice", null, FirstPage));
                await s.Do("removed alice's config", c => c.GetSessionPeerConfigAsync("w", "s", "alice"));
                await s.Do("re-add alice and bob", c => c.AddSessionPeersAsync("w", "s", new Dictionary<string, SessionPeerConfig>
                {
                    ["alice"] = new(),
                    ["bob"] = new(ObserveOthers: false),
                }));

                // Adding keeps carol (a PUT would have replaced the members); this listing tells the two apart.
                await s.Do("members after re-add", c => c.ListSessionPeersAsync("w", "s", FirstPage));
                await s.Do("remove carol, bob and a non-member", c => c.RemoveSessionPeersAsync("w", "s", ["carol", "bob", "nobody"]));
                await s.Do("members after remove", c => c.ListSessionPeersAsync("w", "s", FirstPage));
                await s.Do("remove nothing", c => c.RemoveSessionPeersAsync("w", "s", []));
                await s.Do("add to a missing session", c => c.AddSessionPeersAsync("w", "missing", new Dictionary<string, SessionPeerConfig> { ["alice"] = new() }));
                await s.Do("set on a missing session", c => c.SetSessionPeersAsync("w", "missing", new Dictionary<string, SessionPeerConfig>()));
                await s.Do("remove from a missing session", c => c.RemoveSessionPeersAsync("w", "missing", ["alice"]));
                await s.Do("members of a missing session", c => c.ListSessionPeersAsync("w", "missing", FirstPage));
                await s.Do("config in a missing session", c => c.GetSessionPeerConfigAsync("w", "missing", "alice"));
                await s.Do("add an invalid peer id", c => c.AddSessionPeersAsync("w", "s", new Dictionary<string, SessionPeerConfig> { ["bad id!"] = new() }));
            }),
        new(
            "messages: batch create, list, get, update metadata",
            [
                nameof(INachosClient.CreateMessagesAsync), nameof(INachosClient.ListMessagesAsync),
                nameof(INachosClient.GetMessageAsync), nameof(INachosClient.UpdateMessageAsync),
            ],
            async s =>
            {
                await s.Do("create in a missing session", c => c.CreateMessagesAsync("w", "s", [new MessageCreate("hi", "alice")]));
                await s.Do("workspace", c => c.GetOrCreateWorkspaceAsync("w"));
                await s.Do("session", c => c.GetOrCreateSessionAsync("w", "s"));
                var batch = await s.Do("create a batch", c => c.CreateMessagesAsync("w", "s",
                [
                    new MessageCreate("hello there", "alice", Obj("""{"lang":"en","n":1}""")),
                    new MessageCreate("héllo 😀 漢字", "bob", Obj("""{"lang":"mixed"}"""), new MessageConfiguration(new ReasoningConfiguration(false))),
                    new MessageCreate("backdated", "alice", CreatedAt: new DateTimeOffset(2020, 5, 6, 7, 8, 9, TimeSpan.Zero)),
                ]));
                await s.Do("create a second batch", c => c.CreateMessagesAsync("w", "s", [new MessageCreate("again", "carol")]));
                await s.Do("senders became members", c => c.ListSessionPeersAsync("w", "s", FirstPage));
                await s.Do("list", c => c.ListMessagesAsync("w", "s", null, FirstPage));
                await s.Do("list paged", c => c.ListMessagesAsync("w", "s", null, new PageRequest(2, 2)));
                await s.Do("list reverse", c => c.ListMessagesAsync("w", "s", null, new PageRequest(1, 3, Reverse: true)));
                await s.Do("list by metadata", c => c.ListMessagesAsync("w", "s", Obj("""{"metadata":{"lang":"en"}}"""), FirstPage));
                await s.Do("list by peer", c => c.ListMessagesAsync("w", "s", Obj("""{"peer_id":"alice"}"""), FirstPage));
                await s.Do("list bad filter", c => c.ListMessagesAsync("w", "s", Obj("""{"peer_id":5}"""), FirstPage));
                await s.Do("list in a missing session", c => c.ListMessagesAsync("w", "missing", null, FirstPage));
                var first = batch?[0].Id ?? "unavailable";
                await s.Do("get", c => c.GetMessageAsync("w", "s", first));
                await s.Do("update metadata", c => c.UpdateMessageAsync("w", "s", first, Obj("""{"edited":true}""")));
                await s.Do("update with null metadata", c => c.UpdateMessageAsync("w", "s", first, null));
                await s.Do("get after update", c => c.GetMessageAsync("w", "s", first));
                await s.Do("get a missing message", c => c.GetMessageAsync("w", "s", "missingmessage"));
                await s.Do("update a missing message", c => c.UpdateMessageAsync("w", "s", "missingmessage", Obj("""{"a":1}""")));
                await s.Do("get in a missing session", c => c.GetMessageAsync("w", "missing", first));
                await s.Do("empty batch", c => c.CreateMessagesAsync("w", "s", []));
                await s.Do("101 messages", c => c.CreateMessagesAsync("w", "s", [.. Enumerable.Range(0, 101).Select(i => new MessageCreate($"m{i}", "alice"))]));
                await s.Do("invalid sender id at index 1 (string detail)", c => c.CreateMessagesAsync("w", "s",
                    [new MessageCreate("ok", "alice"), new MessageCreate("bad", "bad id!")]));
                await s.Do("located array detail: content over the limit at index 0", c => c.CreateMessagesAsync("w", "s",
                    [new MessageCreate(new string('x', 25_001), "alice")]));
                await s.Do("nothing was added by the failures", c => c.ListMessagesAsync("w", "s", null, FirstPage));
            }),
    ];

    public static TheoryData<string> ScenarioNames => [.. Scenarios.Select(s => s.Name)];

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public async Task Http_MatchesInProcess(string name)
    {
        var scenario = Scenarios.Single(s => s.Name == name);
        using var harness = new RoundTripHarness();
        var http = new Steps(harness.Http, Side.Http, () => harness.Wire.Requests.Count);
        var inProcess = new Steps(harness.InProcess, Side.InProcess);

        await scenario.Run(http);
        await scenario.Run(inProcess);

        http.Entries.Count.ShouldBe(inProcess.Entries.Count);
        for (var i = 0; i < http.Entries.Count; i++)
        {
            http.Entries[i].ShouldBe(inProcess.Entries[i], $"step {i}: HTTP vs in-process");
        }

        http.Entries.ShouldContain(e => e.Contains(" => ", StringComparison.Ordinal), "a scenario must have successful steps");
        foreach (var steps in new[] { http, inProcess })
        {
            scenario.Covers.Where(op => !steps.Called.Contains(op)).ShouldBeEmpty("declared operations the scenario never called");
        }

        // Every step reached the server exactly once: none is rejected client-side (that would be 0) and none was
        // retried (more than 1). A step added later that the client rejects before sending must change this rule.
        http.Sent.Where(step => step.Requests != 1).Select(step => $"{step.Label}: {step.Requests}").ShouldBeEmpty("requests per step");
        http.Sent.Count.ShouldBe(http.Entries.Count);
    }

    [Fact]
    public void EveryOperation_HasARoundTrip()
    {
        var operations = typeof(INachosClient).GetMethods().Select(m => m.Name).Distinct().Order().ToArray();
        var covered = Scenarios.SelectMany(s => s.Covers).Concat(AdminOperations).ToHashSet();

        operations.Where(o => !covered.Contains(o)).ShouldBeEmpty("INachosClient operations without a round trip");
        covered.Where(c => !operations.Contains(c)).ShouldBeEmpty("covered names that are not operations");
    }

    [Fact]
    public async Task AddGrant_IsNoContent_AndSentOnce()
    {
        using var harness = new RoundTripHarness();

        await harness.Http.AddGrantAsync("00000000-0000-0000-0000-000000000001", null, GrantRoles.Workspace);

        harness.Wire.Requests.ShouldBe(["POST /v3/admin/grants"]);
    }

    [Fact]
    public async Task CreateKey_WithoutConfiguredSigningKeys_Is422_AndNotRetried()
    {
        using var harness = new RoundTripHarness();

        var ex = await Should.ThrowAsync<NachosValidationException>(() => harness.Http.CreateKeyAsync("w"));

        ex.Message.ShouldNotBeNullOrWhiteSpace();
        ex.Message.ShouldNotContain("Not implemented in this Nachos version");
        harness.Wire.Requests.ShouldBe(["POST /v3/keys?workspace_id=w"]);
    }

    [Fact]
    public async Task NonEmptySessionScopes_Is501_OnARetryableRoute_AndNotRetried()
    {
        // INachosClient cannot send scopes, so the request goes through the same named pipeline by hand, carrying the
        // retryable get-or-create route template.
        using var harness = new RoundTripHarness();
        await harness.Http.GetOrCreateWorkspaceAsync("w");
        using var http = harness.CreatePipelineClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "v3/workspaces/w/sessions")
        {
            Content = new StringContent("""{"id":"s","scopes":["team"]}""", Encoding.UTF8, "application/json"),
        };
        request.Options.Set(RetryHandler.RouteTemplate, "/v3/workspaces/{workspace_id}/sessions");

        using var response = await http.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        harness.Wire.Requests.ShouldBe(["POST /v3/workspaces", "POST /v3/workspaces/w/sessions"]);
        (await harness.Http.ListSessionsAsync("w", null, FirstPage)).Total.ShouldBe(0);
    }

    /// <summary>
    /// An invalid body id together with non-empty <c>scopes</c>: the located schema error comes first, the 501 of the
    /// staged scopes gap is never reached, and nothing is stored. <see cref="INachosClient"/> cannot send scopes, so
    /// the request goes through the named pipeline by hand and the response through the client's own mapping.
    /// </summary>
    [Fact]
    public async Task InvalidSessionId_WithNonEmptyScopes_IsTheLocatedSchemaError_NotThe501()
    {
        using var harness = new RoundTripHarness();
        await harness.Http.GetOrCreateWorkspaceAsync("w");
        using var http = harness.CreatePipelineClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "v3/workspaces/w/sessions")
        {
            Content = new StringContent("""{"id":"bad id!","scopes":["team"],"peers":{"not-stored":{}}}""", Encoding.UTF8, "application/json"),
        };
        request.Options.Set(RetryHandler.RouteTemplate, "/v3/workspaces/{workspace_id}/sessions");

        using var response = await http.SendAsync(request);
        var mapped = await MapAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var error = mapped.ShouldBeOfType<RequestValidationException>().Errors.ShouldHaveSingleItem();
        JsonSerializer.Serialize(error.Loc, Compare).ShouldBe("""["body","id"]""");
        error.Type.ShouldBe("string_pattern_mismatch");
        harness.Wire.Requests.ShouldBe(["POST /v3/workspaces", "POST /v3/workspaces/w/sessions"]);
        (await harness.Http.ListSessionsAsync("w", null, FirstPage)).Total.ShouldBe(0);
        (await harness.Http.ListPeersAsync("w", null, null, FirstPage)).Total.ShouldBe(0);
    }

    /// <summary>
    /// A body that is not valid UTF-8 is rejected as a whole, as a located 422 at <c>["body"]</c>. The client always
    /// sends what it serialized (valid UTF-8), so the bytes go through the named pipeline by hand and the response
    /// through the client's own mapping.
    /// </summary>
    [Fact]
    public async Task InvalidUtf8Body_IsAJsonInvalidErrorLocatedAtTheBody()
    {
        using var harness = new RoundTripHarness();
        using var http = harness.CreatePipelineClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "v3/workspaces")
        {
            Content = new ByteArrayContent([(byte)'{', (byte)'"', (byte)'i', (byte)'d', (byte)'"', (byte)':', (byte)'"', 0xFF, 0xFE, (byte)'"', (byte)'}']),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Options.Set(RetryHandler.RouteTemplate, "/v3/workspaces");

        using var response = await http.SendAsync(request);
        var mapped = await MapAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var error = mapped.ShouldBeOfType<RequestValidationException>().Errors.ShouldHaveSingleItem();
        JsonSerializer.Serialize(error.Loc, Compare).ShouldBe("""["body"]""");
        error.Type.ShouldBe("json_invalid");
        harness.Wire.Requests.ShouldBe(["POST /v3/workspaces"]);
        (await harness.Http.ListWorkspacesAsync(null, FirstPage)).Total.ShouldBe(0);
    }

    /// <summary>The exception <see cref="NachosHttpClient"/> would raise for <paramref name="response"/>.</summary>
    private static async Task<Exception> MapAsync(HttpResponseMessage response) =>
        ErrorMapper.Map(response, await response.Content.ReadAsStringAsync(), "POST (by hand)", RedactionSecrets.None, TimeProvider.System);

    [Fact]
    public void Normalization_AliasesOnlyMessageIds()
    {
        var a = new Steps(null!);
        var b = new Steps(null!);
        a.Record("m", new[] { Sample, Sample with { Id = "id-C", Content = "two" } });
        b.Record("m", new[] { Sample with { Id = "id-B" }, Sample with { Id = "id-D", Content = "two" } });

        a.Entries.ShouldBe(b.Entries);
        a.Entries.Single().ShouldContain("<message#0>");
        a.Entries.Single().ShouldContain("<message#1>");
    }

    private static readonly Message Sample = new("id-A", "hi", "alice", "s", Obj("""{"k":1}"""), RoundTripHarness.Start, "w", 1);

    public static TheoryData<string, object, object> DeterministicChanges => new()
    {
        { "content", Sample, Sample with { Content = "other" } },
        { "peer", Sample, Sample with { PeerId = "bob" } },
        { "session", Sample, Sample with { SessionId = "s2" } },
        { "workspace", Sample, Sample with { WorkspaceId = "w2" } },
        { "metadata", Sample, Sample with { Metadata = Obj("""{"k":2}""") } },
        { "created_at", Sample, Sample with { CreatedAt = Sample.CreatedAt.AddTicks(1) } },
        { "token_count", Sample, Sample with { TokenCount = 2 } },
        { "one id for two messages", new[] { Sample, Sample with { Id = "id-B" } }, new[] { Sample, Sample } },
        { "id order", new[] { Sample, Sample with { Id = "id-B" } }, new[] { Sample with { Id = "id-B" }, Sample with { Content = "x" } } },
    };

    /// <summary>
    /// Aliasing is confined to the <c>id</c> property: two messages whose content repeats their own, different ids
    /// stay different (a whole-text replacement would have made them equal), while the ids themselves are aliased.
    /// </summary>
    [Fact]
    public void Normalization_LeavesAnIdLikeStringInContentAlone()
    {
        var a = new Steps(null!);
        var b = new Steps(null!);

        a.Record("m", Sample with { Content = "see " + Sample.Id });
        b.Record("m", Sample with { Id = "id-B", Content = "see id-B" });

        a.Entries.ShouldNotBe(b.Entries);
        a.Entries.Single().ShouldContain("\"id\":\"<message#0>\"");
        a.Entries.Single().ShouldContain("\"content\":\"see id-A\"");
        b.Entries.Single().ShouldContain("\"id\":\"<message#0>\"");
        b.Entries.Single().ShouldContain("\"content\":\"see id-B\"");
    }

    /// <summary>
    /// Aliasing stops at the record's own <c>id</c>: a metadata <c>id</c> that happens to equal the message's generated
    /// id is caller data, so two messages differing only there stay different (a replacement reaching nested values
    /// would alias both and make them equal), while their own ids are aliased; in a page and a list too.
    /// </summary>
    [Theory]
    [InlineData("entity")]
    [InlineData("list")]
    [InlineData("page")]
    public void Normalization_LeavesANestedIdEqualToTheGeneratedIdAlone(string shape)
    {
        var a = new Steps(null!);
        var b = new Steps(null!);
        var left = Sample with { Metadata = Obj("""{"id":"id-A"}""") };
        var right = Sample with { Id = "id-B", Metadata = Obj("""{"id":"id-B"}""") };

        a.Record("m", Shape(left));
        b.Record("m", Shape(right));

        a.Entries.ShouldNotBe(b.Entries);
        a.Entries.Single().ShouldContain("\"id\":\"<message#0>\"");
        a.Entries.Single().ShouldContain("\"metadata\":{\"id\":\"id-A\"}");
        b.Entries.Single().ShouldContain("\"id\":\"<message#0>\"");
        b.Entries.Single().ShouldContain("\"metadata\":{\"id\":\"id-B\"}");

        object Shape(Message message) => shape switch
        {
            "list" => new[] { message },
            "page" => new Page<Message>([message], 1, 1, 1, 1),
            _ => message,
        };
    }

    [Theory]
    [MemberData(nameof(DeterministicChanges))]
    public void Normalization_DoesNotHideADeterministicField(string _, object left, object right)
    {
        var a = new Steps(null!);
        var b = new Steps(null!);

        a.Record("m", left);
        b.Record("m", right);

        a.Entries.ShouldNotBe(b.Entries);
    }

    [Theory]
    [InlineData("created_at")]
    [InlineData("metadata")]
    [InlineData("configuration")]
    public void Normalization_DoesNotHideAWorkspaceField(string field)
    {
        var a = new Steps(null!);
        var b = new Steps(null!);
        var workspace = new Workspace("w", Obj("""{"k":1}"""), Obj("""{"c":1}"""), RoundTripHarness.Start);

        a.Record("w", workspace);
        b.Record("w", field switch
        {
            "created_at" => workspace with { CreatedAt = workspace.CreatedAt.AddSeconds(1) },
            "metadata" => workspace with { Metadata = Obj("""{"k":2}""") },
            _ => workspace with { Configuration = new JsonObject() },
        });

        a.Entries.ShouldNotBe(b.Entries);
    }

    [Fact]
    public void Exceptions_AreRecordedWithTheirDistinguishingDetail()
    {
        var a = new Steps(null!);
        var b = new Steps(null!);

        a.RecordError("e", new RequestValidationException([new ValidationError(["body", "messages", 1, "peer_id"], "bad", "value_error")]));
        b.RecordError("e", new RequestValidationException([new ValidationError(["body", "messages", 2, "peer_id"], "bad", "value_error")]));

        a.Entries.ShouldNotBe(b.Entries);
    }

    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>A named call sequence and the operations it must call.</summary>
    private sealed record Scenario(string Name, string[] Covers, Func<Steps, Task> Run);

    private enum Side
    {
        Http,
        InProcess,
    }

    /// <summary>
    /// Runs steps against one client (through a proxy that records which operations were called) and keeps one line
    /// per step: <c>"label => result"</c> or <c>"label !! exception"</c>, with message ids aliased.
    /// </summary>
    private sealed class Steps
    {
        /// <summary>What the in-process client says about an invalid id (<c>IdValidator</c>, the domain rule).</summary>
        private const string InProcessIdDetail = "id must be 1–512 ASCII letters, digits, '_' or '-'.";

        private readonly INachosClient _client;
        private readonly Side _side;
        private readonly Dictionary<string, string> _messageIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> _called = [];
        private readonly Func<int>? _wireCount;

        /// <param name="client">The client under test; null only for normalization tests that record by hand.</param>
        /// <param name="side">Which client this is, for the steps whose outcome differs by design.</param>
        /// <param name="wireCount">The number of requests the server has seen so far, for <see cref="Sent"/>.</param>
        public Steps(INachosClient client, Side side = Side.Http, Func<int>? wireCount = null)
        {
            _client = client is null ? null! : CallRecorder.Wrap(client, _called);
            _side = side;
            _wireCount = wireCount;
        }

        /// <summary>
        /// A create whose body <c>id</c> is invalid: the one outcome agreed to differ by client. The API rejects it at
        /// admission as schema validation, a located 422 with exactly one error at <c>["body","id"]</c> of
        /// <paramref name="type"/> (<c>string_too_short</c>, <c>string_too_long</c> or <c>string_pattern_mismatch</c>),
        /// which the HTTP client maps to <see cref="RequestValidationException"/>; the in-process client keeps the
        /// domain rule's <see cref="NachosValidationException"/>. Each side is asserted on its own shape here, and the
        /// step is recorded identically on both, so the line-for-line comparison of the scenario is unchanged. Neither
        /// side stores anything, which the scenario's later steps compare as strictly as any other.
        /// </summary>
        public async Task DoInvalidBodyId<T>(string label, string id, string type, Func<INachosClient, Task<T>> call)
        {
            var before = _wireCount?.Invoke();
            Exception? thrown = null;
            try
            {
                await call(_client);
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
            finally
            {
                CountSent(label, before);
            }

            thrown.ShouldNotBeNull($"{label}: the {_side} client accepted the invalid id");
            if (_side == Side.Http)
            {
                var error = thrown.ShouldBeOfType<RequestValidationException>($"{label}: HTTP").Errors.ShouldHaveSingleItem($"{label}: HTTP errors");
                JsonSerializer.Serialize(error.Loc, Compare).ShouldBe("""["body","id"]""", $"{label}: HTTP loc");
                error.Type.ShouldBe(type, $"{label}: HTTP type");
                error.Msg.ShouldNotBeNullOrWhiteSpace($"{label}: HTTP msg");
                (id.Length > 0 && error.Msg.Contains(id, StringComparison.Ordinal)).ShouldBeFalse($"{label}: HTTP msg echoes the id");
            }
            else
            {
                thrown.ShouldBeOfType<NachosValidationException>($"{label}: in-process").Detail.ShouldBe(InProcessIdDetail, $"{label}: in-process detail");
            }

            Entries.Add($"{label} !! invalid body id ({type}): RequestValidationException at [body, id] over HTTP, NachosValidationException in process");
        }

        public List<string> Entries { get; } = [];

        /// <summary>Requests that reached the server during each step (only with a wire count).</summary>
        public List<(string Label, int Requests)> Sent { get; } = [];

        public HashSet<string> Called => _called;

        public async Task<T?> Do<T>(string label, Func<INachosClient, Task<T>> call)
        {
            var before = _wireCount?.Invoke();
            try
            {
                var result = await call(_client);
                Record(label, result);
                return result;
            }
            catch (Exception ex)
            {
                RecordError(label, ex);
                return default;
            }
            finally
            {
                CountSent(label, before);
            }
        }

        public async Task Do(string label, Func<INachosClient, Task> call)
        {
            var before = _wireCount?.Invoke();
            try
            {
                await call(_client);
                Entries.Add($"{label} => (no content)");
            }
            catch (Exception ex)
            {
                RecordError(label, ex);
            }
            finally
            {
                CountSent(label, before);
            }
        }

        private void CountSent(string label, int? before)
        {
            if (before is { } start)
            {
                Sent.Add((label, _wireCount!() - start));
            }
        }

        public void Record(string label, object? result)
        {
            Alias(result);
            Entries.Add($"{label} => {Normalize(JsonSerializer.Serialize(result, Compare))}");
        }

        // Exceptions are recorded as they are: no exception text of either client names a generated message id.
        public void RecordError(string label, Exception ex)
        {
            var detail = ex switch
            {
                RequestValidationException validation => JsonSerializer.Serialize(validation.Errors, Compare),
                NachosValidationException validation => validation.Detail,
                HttpRequestException http => $"{(int?)http.StatusCode} {http.HttpRequestError} {http.Message}",
                _ => ex.Message,
            };
            Entries.Add($"{label} !! {ex.GetType().Name}: {detail}");
        }

        // Message ids are server-generated: each new one gets the next alias, in order of first appearance.
        private void Alias(object? result)
        {
            IEnumerable<Message> messages = result switch
            {
                Message m => [m],
                IEnumerable<Message> list => list,
                Page<Message> page => page.Items,
                _ => [],
            };
            foreach (var message in messages)
            {
                _messageIds.TryAdd(message.Id, $"<message#{_messageIds.Count}>");
            }
        }

        // Only each record's own top-level "id" is aliased: the record itself, each record of a list, or each record
        // of a page's "items". The tree is walked no deeper, so a generated id repeated anywhere else (content, a
        // metadata "id", any nested value) is never touched and still compares as the deterministic data it is.
        private string Normalize(string json)
        {
            var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = Compare.MaxDepth })!;
            switch (root)
            {
                case JsonObject page when page["items"] is JsonArray items:
                    foreach (var item in items)
                    {
                        AliasOwnId(item);
                    }

                    break;
                case JsonArray records:
                    foreach (var record in records)
                    {
                        AliasOwnId(record);
                    }

                    break;
                default:
                    AliasOwnId(root);
                    break;
            }

            // Relaxed escaping keeps the alias readable ("<message#0>", not "<message#0>"); both sides are
            // written the same way, so entries still compare.
            return root.ToJsonString(Readable);
        }

        private static readonly JsonSerializerOptions Readable = new(Compare) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private void AliasOwnId(JsonNode? record)
        {
            if (record is JsonObject entity && entity["id"] is JsonValue value && value.TryGetValue<string>(out var id) && _messageIds.TryGetValue(id, out var alias))
            {
                entity["id"] = alias;
            }
        }
    }

    /// <summary>Forwards every call to the wrapped client and records the operation's name.</summary>
    public class CallRecorder : DispatchProxy
    {
        private INachosClient _target = null!;
        private HashSet<string> _called = null!;

        internal static INachosClient Wrap(INachosClient target, HashSet<string> called)
        {
            var proxy = Create<INachosClient, CallRecorder>();
            var recorder = (CallRecorder)(object)proxy;
            recorder._target = target;
            recorder._called = called;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            _called.Add(targetMethod!.Name);
            try
            {
                return targetMethod.Invoke(_target, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }
}
