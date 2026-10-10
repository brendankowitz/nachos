using System.Text.Json;
using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Shouldly;

namespace Nachos.DataLayer.InMemory.Tests;

/// <summary>
/// JSON handling of the in-memory provider: stored JSON is canonical (what a client could have sent), so values built in
/// C# behave exactly like the same values received over HTTP and stored as text by the SQL provider.
/// </summary>
public sealed class InMemoryJsonTests
{
    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>Metadata values whose CLR type is not the JSON string they serialize to.</summary>
    public static TheoryData<ResourceKind, string> NonStringBackedValues()
    {
        var data = new TheoryData<ResourceKind, string>();
        foreach (var kind in Enum.GetValues<ResourceKind>())
        {
            foreach (var key in ValueKeys)
            {
                data.Add(kind, key);
            }
        }

        return data;
    }

    private static readonly string[] ValueKeys = ["guid", "dateTimeOffset", "dateTime", "char"];

    private static JsonObject CSharpBuiltMetadata() => new()
    {
        ["guid"] = JsonValue.Create(Guid.Parse("11111111-2222-3333-4444-555555555555")),
        ["dateTimeOffset"] = JsonValue.Create(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2))),
        ["dateTime"] = JsonValue.Create(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)),
        ["char"] = JsonValue.Create('Q'),
    };

    /// <summary>The JSON string each value serializes to, which is what a filter compares against.</summary>
    private static string CanonicalText(string key) =>
        JsonSerializer.Deserialize<string>(CSharpBuiltMetadata()[key]!.ToJsonString())!;

    [Theory]
    [MemberData(nameof(NonStringBackedValues))]
    public async Task Filter_OnCSharpBuiltValue_ComparesItsJsonString(ResourceKind kind, string key)
    {
        var text = CanonicalText(key);
        var query = await SeedTwoRowsAsync(kind);

        // "match" carries the C#-built values, "control" does not have the keys at all.
        (await query(Filter(key, Quote(text)))).ShouldBe(["match"]);
        (await query(Filter(key, Quote("x")))).ShouldBeEmpty();
        (await query(Filter(key, Operator("contains", text[..Math.Min(4, text.Length)])))).ShouldBe(["match"]);
        (await query(Filter(key, Operator("icontains", text.ToLowerInvariant())))).ShouldBe(["match"]);
        (await query(Filter(key, Operator("gt", string.Empty)))).ShouldBe(["match"]);
        (await query(Filter(key, Operator("gte", text)))).ShouldBe(["match"]);
        (await query(Filter(key, Operator("gt", text)))).ShouldBeEmpty();
        (await query(Filter(key, Operator("ne", text)))).ShouldBe(["control"]);
    }

    /// <summary><c>{"metadata":{key: operand}}</c>.</summary>
    private static string Filter(string key, string operandJson) => "{\"metadata\":{" + Quote(key) + ":" + operandJson + "}}";

    private static string Operator(string op, string text) => "{" + Quote(op) + ":" + Quote(text) + "}";

    private static string Quote(string text) => JsonSerializer.Serialize(text);

    /// <summary>Creates a "match" row with <see cref="CSharpBuiltMetadata"/> and a "control" row, and returns a query.</summary>
    private static async Task<Func<string, Task<string[]>>> SeedTwoRowsAsync(ResourceKind kind)
    {
        var store = new InMemoryMemoryStore(TimeProvider.System);
        var page = new PageRequest();
        FilterNode Parse(string filter) => FilterParser.Parse(filter, kind)!;

        if (kind == ResourceKind.Workspace)
        {
            await store.Workspaces.GetOrCreateAsync("match", CSharpBuiltMetadata(), null, Ct);
            await store.Workspaces.GetOrCreateAsync("control", null, null, Ct);
            return async filter =>
                [.. (await store.Workspaces.ListAsync(Parse(filter), page, Ct)).Items.Select(w => w.Name)];
        }

        await store.Workspaces.GetOrCreateAsync("ws", null, null, Ct);
        switch (kind)
        {
            case ResourceKind.Peer:
                await store.Peers.GetOrCreateAsync("ws", "match", CSharpBuiltMetadata(), null, Ct);
                await store.Peers.GetOrCreateAsync("ws", "control", null, null, Ct);
                return async filter =>
                    [.. (await store.Peers.ListAsync("ws", PeerKind.All, Parse(filter), page, Ct)).Items.Select(p => p.Name)];
            case ResourceKind.Session:
                await store.Sessions.GetOrCreateAsync("ws", "match", CSharpBuiltMetadata(), null, null, Ct);
                await store.Sessions.GetOrCreateAsync("ws", "control", null, null, null, Ct);
                return async filter =>
                    [.. (await store.Sessions.ListAsync("ws", Parse(filter), page, Ct)).Items.Select(s => s.Name)];
            default:
                await store.Sessions.GetOrCreateAsync("ws", "s", null, null, null, Ct);
                await store.Messages.AppendAsync(
                    "ws", "s", [new("alice", "match", 1, CSharpBuiltMetadata(), null), new("alice", "control", 1, null, null)], null, Ct);
                return async filter =>
                    [.. (await store.Messages.ListAsync("ws", "s", Parse(filter), page, Ct)).Items.Select(m => m.Content)];
        }
    }

    [Fact]
    public async Task CSharpBuiltValues_RoundTripAsCanonicalJson()
    {
        var store = new InMemoryMemoryStore(TimeProvider.System);
        var expectedJson = JsonNode.Parse(CSharpBuiltMetadata().ToJsonString())!.ToJsonString();

        var created = await store.Workspaces.GetOrCreateAsync("ws", CSharpBuiltMetadata(), CSharpBuiltMetadata(), Ct);
        var reread = await store.Workspaces.GetAsync("ws", Ct);

        foreach (var record in new[] { created, reread! })
        {
            record.Metadata.ToJsonString().ShouldBe(expectedJson);
            record.Configuration.ToJsonString().ShouldBe(expectedJson);
            // A Guid comes back as a plain JSON string, readable as one.
            record.Metadata["guid"]!.GetValue<string>().ShouldBe("11111111-2222-3333-4444-555555555555");
            record.Metadata["char"]!.GetValue<string>().ShouldBe("Q");
            record.Metadata["guid"]!.GetValueKind().ShouldBe(JsonValueKind.String);
        }
    }

    public static TheoryData<double> NonFiniteNumbers() => new() { double.NaN, double.PositiveInfinity, double.NegativeInfinity };

    [Theory]
    [MemberData(nameof(NonFiniteNumbers))]
    public async Task NonFiniteNumber_IsRejectedOnEveryWriteAndNothingIsStored(double number)
    {
        var store = new InMemoryMemoryStore(TimeProvider.System);
        JsonObject Bad() => new() { ["n"] = number };

        await ShouldRejectAsync("metadata", () => store.Workspaces.GetOrCreateAsync("bad", Bad(), null, Ct));
        await ShouldRejectAsync("configuration", () => store.Workspaces.GetOrCreateAsync("bad", null, Bad(), Ct));
        (await store.Workspaces.GetAsync("bad", Ct)).ShouldBeNull();

        await store.Workspaces.GetOrCreateAsync("ws", null, null, Ct);
        await ShouldRejectAsync("metadata", () => store.Workspaces.UpdateAsync("ws", Bad(), null, Ct));
        await ShouldRejectAsync("configuration", () => store.Workspaces.UpdateAsync("ws", null, Bad(), Ct));

        await ShouldRejectAsync("metadata", () => store.Peers.GetOrCreateAsync("ws", "bad", Bad(), null, Ct));
        (await store.Peers.GetAsync("ws", "bad", Ct)).ShouldBeNull();
        await store.Peers.GetOrCreateAsync("ws", "p", null, null, Ct);
        await ShouldRejectAsync("configuration", () => store.Peers.UpdateAsync("ws", "p", null, Bad(), Ct));

        await ShouldRejectAsync("configuration", () => store.Sessions.GetOrCreateAsync("ws", "bad", null, Bad(), null, Ct));
        (await store.Sessions.GetAsync("ws", "bad", Ct)).ShouldBeNull();
        await store.Sessions.GetOrCreateAsync("ws", "s", null, null, null, Ct);
        await ShouldRejectAsync("metadata", () => store.Sessions.UpdateAsync("ws", "s", Bad(), null, Ct));

        await ShouldRejectAsync(
            "metadata",
            () => store.Messages.AppendAsync("ws", "s", [new("alice", "ok", 1, null, null), new("alice", "bad", 1, Bad(), null)], null, Ct));
        (await store.Messages.ListAsync("ws", "s", null, new PageRequest(), Ct)).Total.ShouldBe(0);
        (await store.Peers.GetAsync("ws", "alice", Ct)).ShouldBeNull();
        var message = (await store.Messages.AppendAsync("ws", "s", [new("alice", "ok", 1, null, null)], null, Ct))[0];
        await ShouldRejectAsync("metadata", () => store.Messages.UpdateMetadataAsync("ws", "s", message.PublicId, Bad(), Ct));

        // Every rejected update left the stored values alone.
        (await store.Workspaces.GetAsync("ws", Ct))!.Metadata.ToJsonString().ShouldBe("{}");
        (await store.Peers.GetAsync("ws", "p", Ct))!.Configuration.ToJsonString().ShouldBe("{}");
        (await store.Sessions.GetAsync("ws", "s", Ct))!.Metadata.ToJsonString().ShouldBe("{}");
        (await store.Messages.GetAsync("ws", "s", message.PublicId, Ct))!.Metadata.ToJsonString().ShouldBe("{}");
    }

    private static async Task ShouldRejectAsync(string field, Func<Task> write)
    {
        var rejected = await Should.ThrowAsync<NachosValidationException>(write);
        rejected.Detail.ShouldStartWith($"{field} contains a value that is not valid JSON or is nested too deeply.");
    }

    /// <summary>
    /// Parsed lazily, so the duplicate property name survives until the store re-serializes the value.
    /// </summary>
    private static JsonObject DuplicateKeys() => (JsonObject)JsonNode.Parse("""{"k":"a","k":"b"}""")!;

    [Fact]
    public async Task DuplicatePropertyNames_AreRejectedOnEveryWrite_AndListsStillWork()
    {
        var store = new InMemoryMemoryStore(TimeProvider.System);
        await store.Workspaces.GetOrCreateAsync("ws", new JsonObject { ["k"] = "a" }, null, Ct);
        await store.Peers.GetOrCreateAsync("ws", "p", new JsonObject { ["k"] = "a" }, null, Ct);
        await store.Sessions.GetOrCreateAsync("ws", "s", new JsonObject { ["k"] = "a" }, null, null, Ct);
        var message = (await store.Messages.AppendAsync(
            "ws", "s", [new("p", "ok", 1, new JsonObject { ["k"] = "a" }, null)], null, Ct))[0];

        await ShouldRejectAsync("metadata", () => store.Workspaces.GetOrCreateAsync("bad", DuplicateKeys(), null, Ct));
        await ShouldRejectAsync("configuration", () => store.Workspaces.GetOrCreateAsync("bad", null, DuplicateKeys(), Ct));
        await ShouldRejectAsync("metadata", () => store.Workspaces.UpdateAsync("ws", DuplicateKeys(), null, Ct));
        await ShouldRejectAsync("configuration", () => store.Workspaces.UpdateAsync("ws", null, DuplicateKeys(), Ct));

        await ShouldRejectAsync("metadata", () => store.Peers.GetOrCreateAsync("ws", "bad", DuplicateKeys(), null, Ct));
        await ShouldRejectAsync("configuration", () => store.Peers.GetOrCreateAsync("ws", "bad", null, DuplicateKeys(), Ct));
        await ShouldRejectAsync("metadata", () => store.Peers.UpdateAsync("ws", "p", DuplicateKeys(), null, Ct));
        await ShouldRejectAsync("configuration", () => store.Peers.UpdateAsync("ws", "p", null, DuplicateKeys(), Ct));

        await ShouldRejectAsync("metadata", () => store.Sessions.GetOrCreateAsync("ws", "bad", DuplicateKeys(), null, null, Ct));
        await ShouldRejectAsync("configuration", () => store.Sessions.GetOrCreateAsync("ws", "bad", null, DuplicateKeys(), null, Ct));
        await ShouldRejectAsync("metadata", () => store.Sessions.UpdateAsync("ws", "s", DuplicateKeys(), null, Ct));
        await ShouldRejectAsync("configuration", () => store.Sessions.UpdateAsync("ws", "s", null, DuplicateKeys(), Ct));

        await ShouldRejectAsync(
            "metadata", () => store.Messages.AppendAsync("ws", "s", [new("p", "bad", 1, DuplicateKeys(), null)], null, Ct));
        await ShouldRejectAsync("metadata", () => store.Messages.UpdateMetadataAsync("ws", "s", message.PublicId, DuplicateKeys(), Ct));

        // Nothing was stored, so a metadata-filtered list still works for every kind.
        (await store.Workspaces.GetAsync("bad", Ct)).ShouldBeNull();
        (await store.Peers.GetAsync("ws", "bad", Ct)).ShouldBeNull();
        (await store.Sessions.GetAsync("ws", "bad", Ct)).ShouldBeNull();
        var page = new PageRequest();
        const string Filter = """{"metadata":{"k":"a"}}""";
        (await store.Workspaces.ListAsync(FilterParser.Parse(Filter, ResourceKind.Workspace), page, Ct)).Total.ShouldBe(1);
        (await store.Peers.ListAsync("ws", PeerKind.All, FilterParser.Parse(Filter, ResourceKind.Peer), page, Ct)).Total.ShouldBe(1);
        (await store.Sessions.ListAsync("ws", FilterParser.Parse(Filter, ResourceKind.Session), page, Ct)).Total.ShouldBe(1);
        (await store.Messages.ListAsync("ws", "s", FilterParser.Parse(Filter, ResourceKind.Message), page, Ct)).Total.ShouldBe(1);
        (await store.Messages.ListAsync("ws", "s", null, page, Ct)).Total.ShouldBe(1);
    }

    [Fact]
    public async Task NestedTooDeeply_IsRejected_WithoutEchoingTheValue()
    {
        var store = new InMemoryMemoryStore(TimeProvider.System);
        JsonNode deep = new JsonObject { ["secret-leaf"] = 1 };
        for (var i = 0; i < 100; i++)
        {
            deep = new JsonObject { ["n"] = deep };
        }

        var rejected = await Should.ThrowAsync<NachosValidationException>(
            () => store.Workspaces.GetOrCreateAsync("ws", (JsonObject)deep, null, Ct));

        rejected.Detail.ShouldStartWith("metadata contains a value that is not valid JSON or is nested too deeply.");
        rejected.Detail.ShouldNotContain("secret");
        (await store.Workspaces.GetAsync("ws", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task WorkspaceUpdate_StoresCSharpBuiltValuesCanonically()
    {
        var store = new InMemoryMemoryStore(TimeProvider.System);
        await store.Workspaces.GetOrCreateAsync("ws", null, null, Ct);
        var expectedJson = JsonNode.Parse(CSharpBuiltMetadata().ToJsonString())!.ToJsonString();

        var updated = await store.Workspaces.UpdateAsync("ws", CSharpBuiltMetadata(), CSharpBuiltMetadata(), Ct);
        var reread = (await store.Workspaces.GetAsync("ws", Ct))!;

        foreach (var record in new[] { updated, reread })
        {
            record.Metadata.ToJsonString().ShouldBe(expectedJson);
            record.Configuration.ToJsonString().ShouldBe(expectedJson);
            record.Metadata["guid"]!.GetValue<string>().ShouldBe("11111111-2222-3333-4444-555555555555");
            record.Metadata["dateTime"]!.GetValueKind().ShouldBe(JsonValueKind.String);
        }

        // Filters see the JSON string, not the CLR value.
        var match = FilterParser.Parse(
            "{\"metadata\":{\"guid\":" + Quote("11111111-2222-3333-4444-555555555555") + "}}", ResourceKind.Workspace);
        (await store.Workspaces.ListAsync(match, new PageRequest(), Ct)).Total.ShouldBe(1);
    }

    [Fact]
    public async Task ReturnedPeerAndSessionJson_IsIsolatedFromTheStore()
    {
        var store = new InMemoryMemoryStore(TimeProvider.System);
        await store.Workspaces.GetOrCreateAsync("ws", null, null, Ct);
        JsonObject Original() => new() { ["a"] = 1 };
        const string Stored = """{"a":1}""";

        var peers = new List<PeerRecord>
        {
            await store.Peers.GetOrCreateAsync("ws", "p", Original(), Original(), Ct),
            await store.Peers.GetOrCreateAsync("ws", "p", null, null, Ct),
            (await store.Peers.GetAsync("ws", "p", Ct))!,
            (await store.Peers.ListAsync("ws", PeerKind.All, null, new PageRequest(), Ct)).Items[0],
        };
        var sessions = new List<SessionRecord>
        {
            await store.Sessions.GetOrCreateAsync("ws", "s", Original(), Original(), null, Ct),
            await store.Sessions.GetOrCreateAsync("ws", "s", null, null, null, Ct),
            (await store.Sessions.GetAsync("ws", "s", Ct))!,
            (await store.Sessions.ListAsync("ws", null, new PageRequest(), Ct)).Items[0],
        };

        foreach (var json in peers.SelectMany(p => new[] { p.Metadata, p.Configuration })
            .Concat(sessions.SelectMany(s => new[] { s.Metadata, s.Configuration })))
        {
            json.Parent.ShouldBeNull();
            json["a"] = 99;
            json["mutated"] = true;
        }

        var peer = (await store.Peers.GetAsync("ws", "p", Ct))!;
        peer.Metadata.ToJsonString().ShouldBe(Stored);
        peer.Configuration.ToJsonString().ShouldBe(Stored);
        var session = (await store.Sessions.GetAsync("ws", "s", Ct))!;
        session.Metadata.ToJsonString().ShouldBe(Stored);
        session.Configuration.ToJsonString().ShouldBe(Stored);
    }
}
