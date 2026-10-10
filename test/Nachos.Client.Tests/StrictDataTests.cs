using System.Net;
using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Json;
using Nachos.Testing.Json;
using Shouldly;

namespace Nachos.Client.Tests;

/// <summary>
/// Every caller <see cref="JsonNode"/> that goes into a request body (metadata, peer configuration, filters) passes
/// through <see cref="StrictJsonData.ToCanonical"/> first, and the canonical copy is what is serialized. A rejected value
/// fails with the helper's <see cref="NachosValidationException"/> before anything is sent.
/// </summary>
public sealed class StrictDataTests
{
    private const string WorkspaceJson = """{"id":"w1","created_at":"2026-10-08T12:00:00Z"}""";
    private const string PeerJson = """{"id":"alice","workspace_id":"w1","created_at":"2026-10-08T12:00:00Z"}""";
    private const string SessionJson = """{"id":"s1","is_active":true,"workspace_id":"w1","created_at":"2026-10-08T12:00:00Z"}""";
    private const string MessageJson =
        """{"id":"m1","content":"hi","peer_id":"alice","session_id":"s1","created_at":"2026-10-08T12:00:00Z","workspace_id":"w1","token_count":1}""";

    /// <summary>Each JSON-data ingress: the call, the response it needs, and where the value lands in the sent body.</summary>
    private static readonly Dictionary<string, (Func<INachosClient, JsonObject, Task> Call, string Response, Func<JsonNode, JsonNode?> Sent)> Sites = new()
    {
        ["GetOrCreateWorkspace.metadata"] = ((c, v) => c.GetOrCreateWorkspaceAsync("w1", v), WorkspaceJson, b => b["metadata"]),
        ["UpdateWorkspace.metadata"] = ((c, v) => c.UpdateWorkspaceAsync("w1", v), WorkspaceJson, b => b["metadata"]),
        ["GetOrCreatePeer.metadata"] = ((c, v) => c.GetOrCreatePeerAsync("w1", "alice", v), PeerJson, b => b["metadata"]),
        ["GetOrCreatePeer.configuration"] = ((c, v) => c.GetOrCreatePeerAsync("w1", "alice", configuration: v), PeerJson, b => b["configuration"]),
        ["UpdatePeer.metadata"] = ((c, v) => c.UpdatePeerAsync("w1", "alice", v), PeerJson, b => b["metadata"]),
        ["UpdatePeer.configuration"] = ((c, v) => c.UpdatePeerAsync("w1", "alice", configuration: v), PeerJson, b => b["configuration"]),
        ["GetOrCreateSession.metadata"] = ((c, v) => c.GetOrCreateSessionAsync("w1", "s1", v), SessionJson, b => b["metadata"]),
        ["UpdateSession.metadata"] = ((c, v) => c.UpdateSessionAsync("w1", "s1", v), SessionJson, b => b["metadata"]),
        ["UpdateMessage.metadata"] = ((c, v) => c.UpdateMessageAsync("w1", "s1", "m1", v), MessageJson, b => b["metadata"]),
        ["CreateMessages.metadata"] = (
            (c, v) => c.CreateMessagesAsync("w1", "s1", [new MessageCreate("a", "alice"), new MessageCreate("b", "bob", v)]),
            $"[{MessageJson}]",
            b => b["messages"]![1]!["metadata"]),
        ["ListWorkspaces.filters"] = ((c, v) => c.ListWorkspacesAsync(v, new PageRequest()), Page(WorkspaceJson), b => b["filters"]),
        ["ListPeers.filters"] = ((c, v) => c.ListPeersAsync("w1", null, v, new PageRequest()), Page(PeerJson), b => b["filters"]),
        ["ListPeerSessions.filters"] = ((c, v) => c.ListPeerSessionsAsync("w1", "alice", v, new PageRequest()), Page(SessionJson), b => b["filters"]),
        ["ListSessions.filters"] = ((c, v) => c.ListSessionsAsync("w1", v, new PageRequest()), Page(SessionJson), b => b["filters"]),
        ["ListMessages.filters"] = ((c, v) => c.ListMessagesAsync("w1", "s1", v, new PageRequest()), Page(MessageJson), b => b["filters"]),
    };

    public static TheoryData<string> SiteNames() => [.. Sites.Keys];

    public static TheoryData<string, string> RejectedCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var site in Sites.Keys)
        {
            foreach (var kind in new[] { "opaque-getter", "interface-projection", "lone-surrogate-value", "lone-surrogate-key", "duplicate-key", "nested-poco" })
            {
                data.Add(site, kind);
            }
        }

        return data;
    }

    [Fact]
    public void EveryJsonDataParameterOfTheClient_HasASite()
    {
        // Metadata, configuration and filters typed as JsonObject, plus MessageCreate.Metadata inside the batch.
        var parameters = typeof(INachosClient).GetMethods()
            .SelectMany(m => m.GetParameters().Where(p => p.ParameterType == typeof(JsonObject)).Select(p => $"{m.Name[..^"Async".Length]}.{p.Name}"))
            .Append("CreateMessages.metadata")
            .ToHashSet(StringComparer.Ordinal);

        parameters.ShouldBe(Sites.Keys.ToHashSet(StringComparer.Ordinal), ignoreOrder: true);
    }

    [Theory]
    [MemberData(nameof(RejectedCases))]
    public async Task RejectedValue_ThrowsTheHelpersException_BeforeAnySend(string site, string kind)
    {
        var opaque = new CountingGetter();
        var projection = new NameProjection();
        JsonObject Value() => kind switch
        {
            "opaque-getter" => new JsonObject { ["k"] = JsonValue.Create(opaque) },
            "interface-projection" => new JsonObject { ["k"] = StrictJsonSamples.InterfaceProjection(projection) },
            "lone-surrogate-value" => new JsonObject { ["k"] = "a\uD800" },
            "lone-surrogate-key" => new JsonObject { ["\uDC00"] = 1 },
            "duplicate-key" => JsonNode.Parse("""{"a":1,"a":2}""")!.AsObject(),
            "nested-poco" => new JsonObject { ["k"] = new JsonArray(JsonValue.Create(new Uri("https://x.test/"))) },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var expected = Should.Throw<NachosValidationException>(() => StrictJsonData.ToCanonical(Value()));
        var (call, response, _) = Sites[site];
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, response));

        var ex = await Should.ThrowAsync<NachosValidationException>(() => call(Client(stub), Value()));

        ex.Message.ShouldBe(expected.Message);
        stub.Requests.ShouldBeEmpty();
        opaque.Reads.ShouldBe(0, "no getter of a caller type may run");
        projection.ExcludedGetterCalls.ShouldBe(0);
        projection.ToStringCalls.ShouldBe(0);
    }

    [Theory]
    [MemberData(nameof(SiteNames))]
    public async Task AttachedConverter_IsNotInvoked_TheCanonicalLiteralIsSent(string site)
    {
        var converter = new CountingUppercaseConverter();
        var sent = await SentValue(site, new JsonObject { ["k"] = StrictJsonSamples.UppercasedString("abc", converter) });

        sent!.ToJsonString().ShouldBe("""{"k":"abc"}""");
        converter.Calls.ShouldBe(0);
    }

    /// <summary>
    /// An allowlisted value declared through an interface is data by its runtime value: the canonical copy sends
    /// <c>5</c>, whereas serializing the caller's node would write the interface's empty contract <c>{}</c>. This is what
    /// proves the canonical copy, not the original, reaches the wire.
    /// </summary>
    [Theory]
    [MemberData(nameof(SiteNames))]
    public async Task InterfaceDeclaredScalar_IsSentAsItsValue(string site)
    {
        var sent = await SentValue(site, new JsonObject { ["k"] = JsonValue.Create<IComparable>(5) });

        sent!.ToJsonString().ShouldBe("""{"k":5}""");
    }

    /// <summary>
    /// A JSON element carrying a converter (declared as itself or as object) is walked as data; the converter never runs.
    /// </summary>
    [Theory]
    [MemberData(nameof(SiteNames))]
    public async Task CustomizedJsonElement_IsSentAsItsData_WithoutRunningItsConverter(string site)
    {
        using var document = System.Text.Json.JsonDocument.Parse("""{"a":[1,"x"]}""");
        var converter = new CountingMarkerConverter<System.Text.Json.JsonElement>();
        var objectConverter = new CountingMarkerConverter<object>();
        var value = new JsonObject
        {
            ["e"] = StrictJsonSamples.CustomizedElement(document.RootElement, converter),
            ["o"] = StrictJsonSamples.CustomizedElementAsObject(document.RootElement, objectConverter),
        };

        var sent = await SentValue(site, value);

        sent!.ToJsonString().ShouldBe("""{"e":{"a":[1,"x"]},"o":{"a":[1,"x"]}}""");
        converter.Calls.ShouldBe(0);
        objectConverter.Calls.ShouldBe(0);
    }

    [Theory]
    [MemberData(nameof(SiteNames))]
    public async Task AcceptedLiterals_AreSentAsCanonicalJson(string site)
    {
        var value = new JsonObject();
        var expected = new JsonObject();
        foreach (var row in StrictJsonSamples.AllowedScalars())
        {
            var kind = (string)row[0];
            value[kind] = StrictJsonSamples.Scalar(kind);
            expected[kind] = JsonNode.Parse((string)row[1]);
        }

        value["nested"] = new JsonArray(JsonNode.Parse("""{"n":12345678901234567890.5,"z":null}"""), "\U0001F600");
        expected["nested"] = JsonNode.Parse("""[{"n":12345678901234567890.5,"z":null},"😀"]""");

        var sent = await SentValue(site, value);

        JsonNode.DeepEquals(sent, expected).ShouldBeTrue($"sent {sent?.ToJsonString()}");
    }

    /// <summary>
    /// The separate string-field contract: plain string members (here message content) are not JSON data. An unpaired
    /// surrogate is written as U+FFFD by the serializer, so the request carries the same bytes as a literal U+FFFD. The
    /// client pins that behaviour; it makes no claim about how the server's request hash treats the two.
    /// </summary>
    [Fact]
    public async Task StringField_WithALoneSurrogate_IsSentAsReplacementCharacter()
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.Created, "[]"));
        var client = Client(stub);

        await client.CreateMessagesAsync("w1", "s1", [new MessageCreate("a\uD800b", "alice")], "k1");
        await client.CreateMessagesAsync("w1", "s1", [new MessageCreate("a�b", "alice")], "k1");

        stub.Requests[0].Body.ShouldBe(stub.Requests[1].Body);
        JsonNode.Parse(stub.Requests[0].Body!)!["messages"]![0]!["content"]!.GetValue<string>().ShouldBe("a�b");
    }

    [Fact]
    public async Task NullMessageEntry_IsRejectedBeforeSending()
    {
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.Created, "[]"));

        await Should.ThrowAsync<ArgumentException>(() => Client(stub).CreateMessagesAsync("w1", "s1", [null!]));

        stub.Requests.ShouldBeEmpty();
    }

    private static async Task<JsonNode?> SentValue(string site, JsonObject value)
    {
        var (call, response, sent) = Sites[site];
        var stub = new StubHandler((_, _) => StubHandler.Json(HttpStatusCode.OK, response));

        await call(Client(stub), value);

        return sent(JsonNode.Parse(stub.Requests.Single().Body!)!);
    }

    private static string Page(string item) => $$"""{"items":[{{item}}],"total":1,"page":1,"size":50,"pages":1}""";

    private static NachosHttpClient Client(StubHandler stub) =>
        new(new HttpClient(stub), new NachosClientOptions { BaseAddress = new Uri("https://nachos.test/") });

    /// <summary>A caller type whose public getter counts reads; serializing it would run the getter.</summary>
    private sealed class CountingGetter
    {
        public int Reads { get; private set; }

        public string Name
        {
            get
            {
                Reads++;
                return "n";
            }
        }
    }
}
