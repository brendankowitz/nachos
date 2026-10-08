using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Shouldly;

namespace Nachos.DataLayer.InMemory.Tests;

/// <summary>
/// A string with an unpaired surrogate has no JSON form: System.Text.Json would silently write it as U+FFFD, so the
/// store rejects it on every write instead of storing something the caller did not send.
/// </summary>
public sealed class InMemoryLoneSurrogateTests
{
    private static CancellationToken Ct => CancellationToken.None;

    private static readonly DateTimeOffset SeedTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Rejection = " contains a value that is not valid JSON or is nested too deeply.";

    /// <summary>JSON holding an unpaired surrogate somewhere the node tree exposes directly.</summary>
    private static readonly Dictionary<string, Func<JsonObject>> Malformed = new()
    {
        ["leaf: lone high"] = () => new JsonObject { ["k"] = "a\uD800" },
        ["leaf: lone low"] = () => new JsonObject { ["k"] = "\uDC00b" },
        ["leaf: high then text"] = () => new JsonObject { ["k"] = "\uD800x" },
        ["leaf: reversed pair"] = () => new JsonObject { ["k"] = "\uDC00\uD800" },
        ["leaf: char"] = () => new JsonObject { ["k"] = JsonValue.Create('\uD800') },
        ["leaf: in array"] = () => new JsonObject { ["k"] = new JsonArray("ok", "\uDFFF") },
        ["leaf: nested"] = () => Nest(new JsonObject { ["k"] = "\uD800" }, levels: 10),
        ["key: lone high"] = () => new JsonObject { ["\uD800"] = 1 },
        ["key: lone low after text"] = () => new JsonObject { ["x\uDFFF"] = 1 },
        ["key: in array"] = () => new JsonObject { ["k"] = new JsonArray("ok", new JsonObject { ["\uDC00"] = true }) },
        ["key: nested"] = () => Nest(new JsonObject { ["\uDBFF"] = null }, levels: 10),
        ["text: escaped leaf"] = () => (JsonObject)JsonNode.Parse("""{"k":"\uD800"}""")!,
        ["text: escaped key"] = () => (JsonObject)JsonNode.Parse("""{"\uDC00":1}""")!,
    };

    public static TheoryData<string> MalformedCases() => [.. Malformed.Keys];

    private static JsonObject Nest(JsonObject leaf, int levels)
    {
        var node = leaf;
        for (var i = 0; i < levels; i++)
        {
            node = new JsonObject { ["n"] = node };
        }

        return node;
    }

    [Theory]
    [MemberData(nameof(MalformedCases))]
    public async Task UnpairedSurrogate_IsRejectedOnEveryWrite_AndNothingIsStored(string name)
    {
        var bad = Malformed[name];
        var store = new InMemoryMemoryStore(TimeProvider.System);

        await ShouldRejectAsync("metadata", () => store.Workspaces.GetOrCreateAsync("bad", bad(), null, Ct));
        await ShouldRejectAsync("configuration", () => store.Workspaces.GetOrCreateAsync("bad", null, bad(), Ct));
        (await store.Workspaces.GetAsync("bad", Ct)).ShouldBeNull();

        await store.Workspaces.GetOrCreateAsync("ws", null, null, Ct);
        await ShouldRejectAsync("metadata", () => store.Workspaces.UpdateAsync("ws", bad(), null, Ct));
        await ShouldRejectAsync("configuration", () => store.Workspaces.UpdateAsync("ws", null, bad(), Ct));

        await ShouldRejectAsync("metadata", () => store.Peers.GetOrCreateAsync("ws", "bad", bad(), null, Ct));
        await ShouldRejectAsync("configuration", () => store.Peers.GetOrCreateAsync("ws", "bad", null, bad(), Ct));
        (await store.Peers.GetAsync("ws", "bad", Ct)).ShouldBeNull();
        await store.Peers.GetOrCreateAsync("ws", "p", null, null, Ct);
        await ShouldRejectAsync("metadata", () => store.Peers.UpdateAsync("ws", "p", bad(), null, Ct));
        await ShouldRejectAsync("configuration", () => store.Peers.UpdateAsync("ws", "p", null, bad(), Ct));

        await ShouldRejectAsync("metadata", () => store.Sessions.GetOrCreateAsync("ws", "bad", bad(), null, null, Ct));
        await ShouldRejectAsync("configuration", () => store.Sessions.GetOrCreateAsync("ws", "bad", null, bad(), null, Ct));
        (await store.Sessions.GetAsync("ws", "bad", Ct)).ShouldBeNull();
        await store.Sessions.GetOrCreateAsync("ws", "s", null, null, null, Ct);
        await ShouldRejectAsync("metadata", () => store.Sessions.UpdateAsync("ws", "s", bad(), null, Ct));
        await ShouldRejectAsync("configuration", () => store.Sessions.UpdateAsync("ws", "s", null, bad(), Ct));

        await ShouldRejectAsync(
            "metadata",
            () => store.Messages.AppendAsync("ws", "s", [new("alice", "ok", 1, null, null), new("alice", "bad", 1, bad(), null)], null, Ct));
        (await store.Messages.ListAsync("ws", "s", null, new PageRequest(), Ct)).Total.ShouldBe(0);
        (await store.Peers.GetAsync("ws", "alice", Ct)).ShouldBeNull();
        var message = (await store.Messages.AppendAsync("ws", "s", [new("alice", "ok", 1, null, null)], null, Ct))[0];
        await ShouldRejectAsync("metadata", () => store.Messages.UpdateMetadataAsync("ws", "s", message.PublicId, bad(), Ct));

        ShouldReject("metadata", () => store.SeedWorkspace("seeded", SeedTime, bad()));
        (await store.Workspaces.GetAsync("seeded", Ct)).ShouldBeNull();
        ShouldReject("metadata", () => store.SeedPeer("ws", "seeded", SeedTime, bad()));
        (await store.Peers.GetAsync("ws", "seeded", Ct)).ShouldBeNull();
        ShouldReject("metadata", () => store.SeedSession("ws", "seeded", SeedTime, isActive: true, bad()));
        (await store.Sessions.GetAsync("ws", "seeded", Ct)).ShouldBeNull();
        ShouldReject("metadata", () => store.SeedMessage("ws", "s", "seeded", "alice", "bad", 1, SeedTime, bad()));
        (await store.Messages.GetAsync("ws", "s", "seeded", Ct)).ShouldBeNull();

        // Every rejected update left the stored values alone.
        var workspace = (await store.Workspaces.GetAsync("ws", Ct))!;
        var peer = (await store.Peers.GetAsync("ws", "p", Ct))!;
        var session = (await store.Sessions.GetAsync("ws", "s", Ct))!;
        foreach (var json in new[] { workspace.Metadata, workspace.Configuration, peer.Metadata, peer.Configuration, session.Metadata, session.Configuration })
        {
            json.ToJsonString().ShouldBe("{}");
        }

        (await store.Messages.GetAsync("ws", "s", message.PublicId, Ct))!.Metadata.ToJsonString().ShouldBe("{}");
        (await store.Messages.ListAsync("ws", "s", null, new PageRequest(), Ct)).Total.ShouldBe(1);
    }

    /// <summary>Well-formed text that a surrogate check must not mistake for an unpaired surrogate.</summary>
    public static TheoryData<string> WellFormedText() =>
    [
        "😀", // a pair (U+1F600)
        "\U0001D11E", // non-BMP, written as its pair
        "�", // the replacement character itself
        "a􏿿z", // the highest pair, between text
        "😀😀", // adjacent pairs
        "plain",
    ];

    [Theory]
    [MemberData(nameof(WellFormedText))]
    public async Task WellFormedText_IsStoredAndRoundTripsUnchanged(string text)
    {
        var store = new InMemoryMemoryStore(TimeProvider.System);
        JsonObject Value() => new() { [text] = text, ["char"] = JsonValue.Create('é'), ["list"] = new JsonArray(text) };

        var records = new List<JsonObject>();
        var workspace = await store.Workspaces.GetOrCreateAsync("ws", Value(), Value(), Ct);
        records.AddRange([workspace.Metadata, workspace.Configuration]);
        var updated = await store.Workspaces.UpdateAsync("ws", Value(), Value(), Ct);
        records.AddRange([updated.Metadata, updated.Configuration]);
        var peer = await store.Peers.GetOrCreateAsync("ws", "p", Value(), Value(), Ct);
        records.AddRange([peer.Metadata, peer.Configuration]);
        var session = await store.Sessions.GetOrCreateAsync("ws", "s", Value(), Value(), null, Ct);
        records.AddRange([session.Metadata, session.Configuration]);
        var message = (await store.Messages.AppendAsync("ws", "s", [new("p", "m", 1, Value(), null)], null, Ct))[0];
        records.Add(message.Metadata);
        records.Add((await store.Messages.UpdateMetadataAsync("ws", "s", message.PublicId, Value(), Ct)).Metadata);
        store.SeedMessage("ws", "s", "seeded", "p", "m", 1, SeedTime, Value());
        records.Add((await store.Messages.GetAsync("ws", "s", "seeded", Ct))!.Metadata);
        records.Add((await store.Workspaces.GetAsync("ws", Ct))!.Metadata);

        foreach (var record in records)
        {
            record[text]!.GetValue<string>().ShouldBe(text);
            record["char"]!.GetValue<string>().ShouldBe("é");
            record["list"]![0]!.GetValue<string>().ShouldBe(text);
        }

        var filter = FilterParser.Parse(
            new JsonObject { ["metadata"] = new JsonObject { [text] = text } }.ToJsonString(), ResourceKind.Workspace);
        (await store.Workspaces.ListAsync(filter, new PageRequest(), Ct)).Total.ShouldBe(1);
    }

    [Fact]
    public async Task VeryDeepNesting_GivesTheValidationError_NotAStackOverflow()
    {
        var store = new InMemoryMemoryStore(TimeProvider.System);
        foreach (var leaf in new[] { "fine", "\uD800" })
        {
            var deep = Nest(new JsonObject { ["k"] = leaf }, levels: 5000);

            await ShouldRejectAsync("metadata", () => store.Workspaces.GetOrCreateAsync("ws", deep, null, Ct));
            (await store.Workspaces.GetAsync("ws", Ct)).ShouldBeNull();
        }
    }

    private static async Task ShouldRejectAsync(string field, Func<Task> write)
    {
        var rejected = await Should.ThrowAsync<NachosValidationException>(write);
        rejected.Detail.ShouldBe(field + Rejection);
    }

    private static void ShouldReject(string field, Action write)
    {
        var rejected = Should.Throw<NachosValidationException>(write);
        rejected.Detail.ShouldBe(field + Rejection);
    }
}
