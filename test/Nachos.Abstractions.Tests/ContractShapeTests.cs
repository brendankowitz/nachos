using System.Text.Json;
using System.Text.Json.Nodes;
using Nachos.Abstractions.Contracts;
using Shouldly;

namespace Nachos.Abstractions.Tests;

public sealed class ContractShapeTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static JsonObject Obj() => new() { ["k"] = "v" };

    private static readonly ReasoningConfiguration Reasoning = new(true, "r");
    private static readonly PeerCardConfiguration PeerCard = new(true, false, "p");
    private static readonly SummaryConfiguration Summary = new(true, 10, 20, "s");
    private static readonly DreamConfiguration Dream = new(true, "d");
    private static readonly DialecticConfiguration Dialectic = new("x");

    private static readonly Workspace SampleWorkspace = new("w", Obj(), Obj(), Now);
    private static readonly Peer SamplePeer = new("p", "w", Now, Obj(), Obj());
    private static readonly Session SampleSession = new("s", true, "w", Obj(), Obj(), Now);
    private static readonly Message SampleMessage = new("m", "hi", "p", "s", Obj(), Now, "w", 1);

    /// <summary>(manifest schema, sample, whether the DTO must carry exactly the manifest's property set).</summary>
    public static TheoryData<string, object, bool> Samples => new()
    {
        { "Workspace", SampleWorkspace, true },
        { "Peer", SamplePeer, true },
        { "Session", SampleSession, true },
        { "Message", SampleMessage, true },
        { "MessageCreate", new MessageCreate("hi", "p", Obj(), new MessageConfiguration(Reasoning), Now), true },
        { "SessionPeerConfig", new SessionPeerConfig(true, false), true },
        { "Page_Workspace_", new Page<Workspace>([SampleWorkspace], 1, 1, 50, 1), true },
        { "Page_Peer_", new Page<Peer>([SamplePeer], 1, 1, 50, 1), true },
        { "Page_Session_", new Page<Session>([SampleSession], 1, 1, 50, 1), true },
        { "Page_Message_", new Page<Message>([SampleMessage], 1, 1, 50, 1), true },
        { "WorkspaceConfiguration", new WorkspaceConfiguration(Reasoning, PeerCard, Summary, Dream, Dialectic, "c"), true },
        { "SessionConfiguration", new SessionConfiguration(Reasoning, PeerCard, Summary, Dream, Dialectic, "c"), true },
        { "MessageConfiguration", new MessageConfiguration(Reasoning), true },
        { "ReasoningConfiguration", Reasoning, true },
        { "PeerCardConfiguration", PeerCard, true },
        { "SummaryConfiguration", Summary, true },
        { "DreamConfiguration", Dream, true },
        { "DialecticConfiguration", Dialectic, true },
        // Nachos emits only loc/msg/type; the manifest's optional ctx/input are never produced.
        { "ValidationError", new ValidationError(["body", 0, "id"], "bad", "value_error"), false },
        // Nachos adds the RFC 9457 type/title/status members next to the manifest's detail.
        { "ErrorResponse", new ErrorResponse("nope", "about:blank", "Not Found", 404), false },
        // Same, for the HTTPValidationError body.
        { "HTTPValidationError", new ValidationErrorResponse([new ValidationError(["body"], "bad", "value_error")], "about:blank", "Unprocessable Entity", 422), false },
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void Dto_SerializesManifestFields(string schema, object sample, bool exact)
    {
        var node = JsonSerializer.SerializeToNode(sample, sample.GetType())!.AsObject();
        var emitted = node.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var manifest = WireManifest.Properties(schema);

        if (exact)
        {
            emitted.ShouldBe(manifest, ignoreOrder: true);
        }
        else if (schema == "ValidationError")
        {
            emitted.ShouldBeSubsetOf(manifest);
        }
        else
        {
            manifest.ShouldBeSubsetOf(emitted);
        }

        foreach (var required in WireManifest.Required(schema))
        {
            node[required].ShouldNotBeNull($"{schema}.{required} is required");
        }
    }

    [Fact]
    public void Page_SerializesPageField()
    {
        var json = JsonSerializer.Serialize(new Page<string>(["a"], 1, 2, 50, 1));

        json.ShouldContain("\"page\":2");
    }

    [Fact]
    public void KeyResponse_SerializesKey()
    {
        JsonSerializer.Serialize(new KeyResponse("abc")).ShouldBe("{\"key\":\"abc\"}");
    }

    [Fact]
    public void Message_RoundTripsThroughJson()
    {
        var json = JsonSerializer.Serialize(SampleMessage);

        JsonSerializer.Deserialize<Message>(json)!.ShouldSatisfyAllConditions(
            m => m.Id.ShouldBe("m"),
            m => m.CreatedAt.ShouldBe(Now),
            m => m.TokenCount.ShouldBe(1),
            m => m.Metadata["k"]!.GetValue<string>().ShouldBe("v"));
    }
}