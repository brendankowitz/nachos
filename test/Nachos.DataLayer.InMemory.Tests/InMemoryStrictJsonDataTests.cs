using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Testing.Json;
using Shouldly;

namespace Nachos.DataLayer.InMemory.Tests;

/// <summary>
/// The provider's acceptance matrix for strict JSON data (see <c>StrictJsonData</c>): every write path that takes caller
/// JSON, the test-only seeding included, accepts exactly the allowlisted data, stores its canonical JSON, and rejects
/// everything else with a field-named 422 that leaves the whole store unchanged.
/// </summary>
public sealed class InMemoryStrictJsonDataTests
{
    private static CancellationToken Ct => CancellationToken.None;

    private static readonly DateTimeOffset SeedTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string Rejection = " contains a value that is not valid JSON or is nested too deeply.";

    private static JsonObject Keep() => new() { ["keep"] = 1 };

    // ---------------------------------------------------------------- write paths

    /// <summary>A write of caller JSON into one field, and a read-back of that stored field.</summary>
    private sealed record Target(Func<JsonObject, Task> Write, Func<Task<JsonObject?>> Read);

    /// <summary>An entry point that accepts caller JSON, with the setup it needs (parents, a record to update).</summary>
    private sealed record WritePath(string Field, Func<InMemoryMemoryStore, Task<Target>> Arrange);

    /// <summary>Seeding is synchronous; a throw becomes a faulted task like every other path's.</summary>
    private static Task Sync(Action write)
    {
        try
        {
            write();
            return Task.CompletedTask;
        }
        catch (Exception e)
        {
            return Task.FromException(e);
        }
    }

    private static async Task<InMemoryMemoryStore> WithSessionAsync(InMemoryMemoryStore store)
    {
        await store.Workspaces.GetOrCreateAsync("ws", null, null, Ct);
        await store.Sessions.GetOrCreateAsync("ws", "s", null, null, null, Ct);
        return store;
    }

    private static readonly Dictionary<string, WritePath> Paths = new()
    {
        ["workspace.create.metadata"] = new("metadata", store => Task.FromResult(new Target(
            p => store.Workspaces.GetOrCreateAsync("t", p, null, Ct),
            async () => (await store.Workspaces.GetAsync("t", Ct))?.Metadata))),
        ["workspace.create.configuration"] = new("configuration", store => Task.FromResult(new Target(
            p => store.Workspaces.GetOrCreateAsync("t", null, p, Ct),
            async () => (await store.Workspaces.GetAsync("t", Ct))?.Configuration))),
        ["workspace.update.metadata"] = new("metadata", async store =>
        {
            await store.Workspaces.GetOrCreateAsync("t", Keep(), Keep(), Ct);
            return new Target(
                p => store.Workspaces.UpdateAsync("t", p, null, Ct),
                async () => (await store.Workspaces.GetAsync("t", Ct))?.Metadata);
        }),
        ["workspace.update.configuration"] = new("configuration", async store =>
        {
            await store.Workspaces.GetOrCreateAsync("t", Keep(), Keep(), Ct);
            return new Target(
                p => store.Workspaces.UpdateAsync("t", null, p, Ct),
                async () => (await store.Workspaces.GetAsync("t", Ct))?.Configuration);
        }),
        ["peer.create.metadata"] = new("metadata", async store =>
        {
            await store.Workspaces.GetOrCreateAsync("ws", null, null, Ct);
            return new Target(
                p => store.Peers.GetOrCreateAsync("ws", "t", p, null, Ct),
                async () => (await store.Peers.GetAsync("ws", "t", Ct))?.Metadata);
        }),
        ["peer.create.configuration"] = new("configuration", async store =>
        {
            await store.Workspaces.GetOrCreateAsync("ws", null, null, Ct);
            return new Target(
                p => store.Peers.GetOrCreateAsync("ws", "t", null, p, Ct),
                async () => (await store.Peers.GetAsync("ws", "t", Ct))?.Configuration);
        }),
        ["peer.update.metadata"] = new("metadata", async store =>
        {
            await store.Workspaces.GetOrCreateAsync("ws", null, null, Ct);
            await store.Peers.GetOrCreateAsync("ws", "t", Keep(), Keep(), Ct);
            return new Target(
                p => store.Peers.UpdateAsync("ws", "t", p, null, Ct),
                async () => (await store.Peers.GetAsync("ws", "t", Ct))?.Metadata);
        }),
        ["peer.update.configuration"] = new("configuration", async store =>
        {
            await store.Workspaces.GetOrCreateAsync("ws", null, null, Ct);
            await store.Peers.GetOrCreateAsync("ws", "t", Keep(), Keep(), Ct);
            return new Target(
                p => store.Peers.UpdateAsync("ws", "t", null, p, Ct),
                async () => (await store.Peers.GetAsync("ws", "t", Ct))?.Configuration);
        }),
        ["session.create.metadata"] = new("metadata", async store =>
        {
            await store.Workspaces.GetOrCreateAsync("ws", null, null, Ct);
            return new Target(
                p => store.Sessions.GetOrCreateAsync("ws", "t", p, null, null, Ct),
                async () => (await store.Sessions.GetAsync("ws", "t", Ct))?.Metadata);
        }),
        ["session.create.configuration"] = new("configuration", async store =>
        {
            await store.Workspaces.GetOrCreateAsync("ws", null, null, Ct);
            return new Target(
                p => store.Sessions.GetOrCreateAsync("ws", "t", null, p, null, Ct),
                async () => (await store.Sessions.GetAsync("ws", "t", Ct))?.Configuration);
        }),
        ["session.update.metadata"] = new("metadata", async store =>
        {
            await store.Workspaces.GetOrCreateAsync("ws", null, null, Ct);
            await store.Sessions.GetOrCreateAsync("ws", "t", Keep(), Keep(), null, Ct);
            return new Target(
                p => store.Sessions.UpdateAsync("ws", "t", p, null, Ct),
                async () => (await store.Sessions.GetAsync("ws", "t", Ct))?.Metadata);
        }),
        ["session.update.configuration"] = new("configuration", async store =>
        {
            await store.Workspaces.GetOrCreateAsync("ws", null, null, Ct);
            await store.Sessions.GetOrCreateAsync("ws", "t", Keep(), Keep(), null, Ct);
            return new Target(
                p => store.Sessions.UpdateAsync("ws", "t", null, p, Ct),
                async () => (await store.Sessions.GetAsync("ws", "t", Ct))?.Configuration);
        }),
        ["message.append.metadata"] = new("metadata", async store =>
        {
            await WithSessionAsync(store);
            return new Target(
                p => store.Messages.AppendAsync("ws", "s", [new("alice", "m", 1, p, null)], null, Ct),
                async () => (await store.Messages.ListAsync("ws", "s", null, new PageRequest(), Ct)).Items.SingleOrDefault()?.Metadata);
        }),
        ["message.update.metadata"] = new("metadata", async store =>
        {
            await WithSessionAsync(store);
            var message = (await store.Messages.AppendAsync("ws", "s", [new("alice", "m", 1, Keep(), null)], null, Ct))[0];
            return new Target(
                p => store.Messages.UpdateMetadataAsync("ws", "s", message.PublicId, p, Ct),
                async () => (await store.Messages.GetAsync("ws", "s", message.PublicId, Ct))?.Metadata);
        }),
        ["seed.workspace.metadata"] = new("metadata", store => Task.FromResult(new Target(
            p => Sync(() => store.SeedWorkspace("t", SeedTime, p)),
            async () => (await store.Workspaces.GetAsync("t", Ct))?.Metadata))),
        ["seed.peer.metadata"] = new("metadata", async store =>
        {
            await store.Workspaces.GetOrCreateAsync("ws", null, null, Ct);
            return new Target(
                p => Sync(() => store.SeedPeer("ws", "t", SeedTime, p)),
                async () => (await store.Peers.GetAsync("ws", "t", Ct))?.Metadata);
        }),
        ["seed.session.metadata"] = new("metadata", async store =>
        {
            await store.Workspaces.GetOrCreateAsync("ws", null, null, Ct);
            return new Target(
                p => Sync(() => store.SeedSession("ws", "t", SeedTime, isActive: true, p)),
                async () => (await store.Sessions.GetAsync("ws", "t", Ct))?.Metadata);
        }),
        ["seed.message.metadata"] = new("metadata", async store =>
        {
            await WithSessionAsync(store);
            store.SeedPeer("ws", "alice", SeedTime, new JsonObject());
            return new Target(
                p => Sync(() => store.SeedMessage("ws", "s", "t", "alice", "m", 1, SeedTime, p)),
                async () => (await store.Messages.GetAsync("ws", "s", "t", Ct))?.Metadata);
        }),
    };

    /// <summary>Every row of the store reachable through the public API, so "nothing stored" covers parents and side effects.</summary>
    private static async Task<string> DumpAsync(InMemoryMemoryStore store)
    {
        var page = new PageRequest(1, PageRequest.MaxSize);
        var dump = new StringBuilder();
        foreach (var workspace in (await store.Workspaces.ListAsync(null, page, Ct)).Items)
        {
            dump.Append(CultureInfo.InvariantCulture, $"W {workspace.Name} {workspace.State} {workspace.Metadata.ToJsonString()} {workspace.Configuration.ToJsonString()}\n");
            foreach (var peer in (await store.Peers.ListAsync(workspace.Name, PeerKind.All, null, page, Ct)).Items)
            {
                dump.Append(CultureInfo.InvariantCulture, $"P {peer.Name} {peer.Metadata.ToJsonString()} {peer.Configuration.ToJsonString()}\n");
            }

            foreach (var session in (await store.Sessions.ListAsync(workspace.Name, null, page, Ct)).Items)
            {
                dump.Append(CultureInfo.InvariantCulture, $"S {session.Name} {session.State} {session.Metadata.ToJsonString()} {session.Configuration.ToJsonString()}\n");
                foreach (var message in (await store.Messages.ListAsync(workspace.Name, session.Name, null, page, Ct)).Items)
                {
                    dump.Append(CultureInfo.InvariantCulture, $"M {message.PublicId} {message.Seq} {message.PeerName} {message.Metadata.ToJsonString()}\n");
                }
            }
        }

        return dump.ToString();
    }

    // ---------------------------------------------------------------- accepted values

    /// <summary>Containers only: the root object plus (containers - 1) alternating arrays and objects, ending in a string.</summary>
    private static JsonObject Nest(int containers)
    {
        JsonNode node = "ok";
        for (var i = 0; i < containers - 1; i++)
        {
            node = i % 2 == 0 ? new JsonArray(node) : new JsonObject { ["k"] = node };
        }

        return new JsonObject { ["k"] = node };
    }

    private const string BigNumber = "12345678901234567890123.456789012345678901234567890";

    /// <summary>Accepted inputs (a fresh tree per call) and the canonical JSON each must be stored as.</summary>
    private static readonly Dictionary<string, (Func<JsonObject> Input, string Json)> Accepted = BuildAccepted();

    private static Dictionary<string, (Func<JsonObject> Input, string Json)> BuildAccepted()
    {
        var accepted = new Dictionary<string, (Func<JsonObject>, string)>();
        foreach (var row in StrictJsonSamples.AllowedScalars())
        {
            var kind = (string)row[0];
            accepted["scalar: " + kind] = (() => new JsonObject { ["k"] = StrictJsonSamples.Scalar(kind) }, $$"""{"k":{{row[1]}}}""");
        }

        accepted["replacement char kept"] = (
            () => new JsonObject { ["\uFFFD"] = "a\uFFFDb", ["c"] = JsonValue.Create('\uFFFD'), ["e"] = JsonNode.Parse("\"\\uFFFD\"") },
            """{"\uFFFD":"a\uFFFDb","c":"\uFFFD","e":"\uFFFD"}""");
        accepted["valid surrogate pairs"] = (
            () => new JsonObject { ["\U0001F600"] = "\U0001D11E", ["e"] = JsonNode.Parse("\"\\uD83D\\uDE00\"") },
            """{"\uD83D\uDE00":"\uD834\uDD1E","e":"\uD83D\uDE00"}""");
        accepted["depth 63"] = (() => Nest(63), Nest(63).ToJsonString());
        accepted["depth 64"] = (() => Nest(64), Nest(64).ToJsonString());
        accepted["json-backed object"] = (
            () => (JsonObject)JsonNode.Parse($$"""{"n":{{BigNumber}},"a":[1,"x",null,{"b":true}],"s":"\u00e9"}""")!,
            $$"""{"n":{{BigNumber}},"a":[1,"x",null,{"b":true}],"s":"\u00e9"}""");
        // JsonValue.Create(JsonElement) takes scalars only; object and array elements are covered by the converter test.
        accepted["json-element values"] = (
            () => new JsonObject
            {
                ["n"] = JsonValue.Create(Element(BigNumber)),
                ["d"] = JsonValue.Create(Element("1.50")),
                ["s"] = JsonValue.Create(Element("\"y\\u00e9\"")),
                ["t"] = JsonValue.Create(Element("true")),
            },
            $$"""{"n":{{BigNumber}},"d":1.50,"s":"y\u00e9","t":true}""");
        return accepted;
    }

    public static TheoryData<string, string> AcceptedRows() => Rows(Accepted.Keys);

    [Theory]
    [MemberData(nameof(AcceptedRows))]
    public async Task AcceptedValue_IsStoredAsItsCanonicalJson_OnEveryPath(string path, string value)
    {
        var store = new InMemoryMemoryStore(TimeProvider.System);
        var target = await Paths[path].Arrange(store);
        var (input, json) = Accepted[value];

        await target.Write(input());

        var stored = (await target.Read()).ShouldNotBeNull();
        stored.ToJsonString().ShouldBe(JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 100 })!.ToJsonString());
    }

    // ---------------------------------------------------------------- rejected values

    private static readonly string[] OneString = ["a"];

    private static readonly object[] Mixed = [1, "a"];

    /// <summary>A value outside the allowlist, a fresh tree per call, and the runtime type the message must name.</summary>
    private static readonly Dictionary<string, (Func<JsonNode> Create, Type Type)> DisallowedTypes = new()
    {
        ["List<string>"] = (() => JsonValue.Create(new List<string> { "a" })!, typeof(List<string>)),
        ["string[]"] = (() => JsonValue.Create(OneString)!, typeof(string[])),
        ["Dictionary<string,object>"] = (() => JsonValue.Create(new Dictionary<string, object> { ["a"] = 1 })!, typeof(Dictionary<string, object>)),
        ["POCO"] = (() => JsonValue.Create(new NameProjection())!, typeof(NameProjection)),
        ["enum"] = (() => JsonValue.Create(DayOfWeek.Monday)!, typeof(DayOfWeek)),
        ["interface"] = (() => StrictJsonSamples.InterfaceProjection(new NameProjection()), typeof(NameProjection)),
        ["Half"] = (() => JsonValue.Create((Half)1.5f)!, typeof(Half)),
        ["Int128"] = (() => JsonValue.Create((Int128)5)!, typeof(Int128)),
        ["JsonNode in a typed value"] = (() => JsonValue.Create(new List<JsonNode> { "ok" })!, typeof(List<JsonNode>)),
        ["object[]"] = (() => JsonValue.Create(Mixed)!, typeof(object[])),
        ["extension data"] = (() => JsonValue.Create(new WithExtensionData())!, typeof(WithExtensionData)),
        ["nested in an array"] = (() => new JsonArray(new JsonObject { ["x"] = JsonValue.Create(DayOfWeek.Friday) }), typeof(DayOfWeek)),
    };

    public static TheoryData<string, string> DisallowedTypeRows() => Rows(DisallowedTypes.Keys);

    [Theory]
    [MemberData(nameof(DisallowedTypeRows))]
    public async Task DisallowedType_IsRejectedNamingFieldAndType_AndNothingIsStored(string path, string value)
    {
        var store = new InMemoryMemoryStore(TimeProvider.System);
        var (field, arrange) = Paths[path];
        var target = await arrange(store);
        var (create, type) = DisallowedTypes[value];
        var before = await DumpAsync(store);

        var rejected = await Should.ThrowAsync<NachosValidationException>(() => target.Write(new JsonObject { ["k"] = create() }));

        rejected.Detail.ShouldStartWith(field + Rejection);
        rejected.Detail.ShouldContain(type.ToString());
        rejected.Detail.ShouldNotContain("surrogate");
        rejected.InnerException.ShouldBeOfType<NachosValidationException>();
        (await DumpAsync(store)).ShouldBe(before);
    }

    /// <summary>Strict-data failures that are not about a CLR type: each keeps the field-named wording.</summary>
    private static readonly Dictionary<string, Func<JsonObject>> InvalidData = new()
    {
        ["lone surrogate string"] = () => new JsonObject { ["k"] = "a\uD800" },
        ["lone surrogate char"] = () => new JsonObject { ["k"] = JsonValue.Create('\uDC00') },
        ["lone surrogate key"] = () => new JsonObject { ["k\uDBFF"] = 1 },
        ["escaped lone surrogate string"] = () => (JsonObject)JsonNode.Parse("""{"k":"\uD800"}""")!,
        ["escaped lone surrogate key"] = () => (JsonObject)JsonNode.Parse("""{"\uDC00":1}""")!,
        ["escaped lone surrogate in element"] = () => new JsonObject { ["k"] = StrictJsonSamples.CustomizedElement(Element("""["\uD800"]"""), new()) },
        ["duplicate key"] = () => (JsonObject)JsonNode.Parse("""{"k":1,"k":2}""")!,
        ["nested duplicate key"] = () => (JsonObject)JsonNode.Parse("""{"a":[{"k":1,"k":2}]}""")!,
        ["duplicate key in element"] = () => new JsonObject { ["k"] = StrictJsonSamples.CustomizedElement(Element("""{"k":1,"k":2}"""), new()) },
        ["depth 65"] = () => Nest(65),
        ["NaN"] = () => new JsonObject { ["k"] = double.NaN },
        ["infinite float"] = () => new JsonObject { ["k"] = float.PositiveInfinity },
    };

    public static TheoryData<string, string> InvalidDataRows() => Rows(InvalidData.Keys);

    [Theory]
    [MemberData(nameof(InvalidDataRows))]
    public async Task InvalidData_IsRejectedNamingTheField_AndNothingIsStored(string path, string value)
    {
        var store = new InMemoryMemoryStore(TimeProvider.System);
        var (field, arrange) = Paths[path];
        var target = await arrange(store);
        var before = await DumpAsync(store);

        var rejected = await Should.ThrowAsync<NachosValidationException>(() => target.Write(InvalidData[value]()));

        rejected.Detail.ShouldStartWith(field + Rejection);
        rejected.Detail.ShouldNotContain("\uD800");
        rejected.InnerException.ShouldBeOfType<NachosValidationException>();
        (await DumpAsync(store)).ShouldBe(before);
    }

    // ---------------------------------------------------------------- caller code never runs

    public static TheoryData<string> PathNames() => [.. Paths.Keys];

    [Theory]
    [MemberData(nameof(PathNames))]
    public async Task CallerConverters_NeverRun_AndTheLiteralDataIsStored(string path)
    {
        var store = new InMemoryMemoryStore(TimeProvider.System);
        var target = await Paths[path].Arrange(store);
        var uppercase = new CountingUppercaseConverter();
        var marker = new CountingMarkerConverter<JsonElement>();
        var objectMarker = new CountingMarkerConverter<object>();
        using var document = JsonDocument.Parse("""{"a":[1,"x"]}""");

        await target.Write(new JsonObject
        {
            ["s"] = StrictJsonSamples.UppercasedString("abc", uppercase),
            ["e"] = StrictJsonSamples.CustomizedElement(document.RootElement, marker),
            ["o"] = StrictJsonSamples.CustomizedElementAsObject(document.RootElement, objectMarker),
        });

        (await target.Read()).ShouldNotBeNull().ToJsonString().ShouldBe("""{"s":"abc","e":{"a":[1,"x"]},"o":{"a":[1,"x"]}}""");
        uppercase.Calls.ShouldBe(0);
        marker.Calls.ShouldBe(0);
        objectMarker.Calls.ShouldBe(0);
    }

    [Theory]
    [MemberData(nameof(PathNames))]
    public async Task RejectedProjection_RunsNoGetterOrToString(string path)
    {
        var store = new InMemoryMemoryStore(TimeProvider.System);
        var target = await Paths[path].Arrange(store);
        var direct = new NameProjection();
        var projected = new NameProjection();

        await Should.ThrowAsync<NachosValidationException>(() => target.Write(new JsonObject { ["k"] = JsonValue.Create(direct) }));
        await Should.ThrowAsync<NachosValidationException>(
            () => target.Write(new JsonObject { ["k"] = StrictJsonSamples.InterfaceProjection(projected) }));

        foreach (var projection in new[] { direct, projected })
        {
            projection.ExcludedGetterCalls.ShouldBe(0);
            projection.ToStringCalls.ShouldBe(0);
        }
    }

    // ---------------------------------------------------------------- stored form

    /// <summary>
    /// C#-built numbers are stored as their JSON number text, so metadata filters compare them exactly like the same
    /// numbers sent as JSON.
    /// </summary>
    [Fact]
    public async Task Filters_OverCSharpBuiltNumbers_CompareTheirJsonValue()
    {
        var store = new InMemoryMemoryStore(TimeProvider.System);
        await store.Workspaces.GetOrCreateAsync(
            "match",
            new JsonObject { ["d"] = 0.1, ["f"] = 1.5f, ["m"] = 12.50m, ["u"] = 18000000000000000000UL, ["i"] = -5 },
            null,
            Ct);
        await store.Workspaces.GetOrCreateAsync("control", new JsonObject { ["d"] = 0.2, ["f"] = 2.5f, ["m"] = 1m, ["u"] = 1UL, ["i"] = 5 }, null, Ct);

        async Task<string[]> Query(string filter) =>
            [.. (await store.Workspaces.ListAsync(FilterParser.Parse(filter, ResourceKind.Workspace), new PageRequest(), Ct)).Items.Select(w => w.Name)];

        (await Query("""{"metadata":{"d":0.1}}""")).ShouldBe(["match"]);
        (await Query("""{"metadata":{"d":1e-1}}""")).ShouldBe(["match"]);
        (await Query("""{"metadata":{"f":1.50}}""")).ShouldBe(["match"]);
        (await Query("""{"metadata":{"m":12.5}}""")).ShouldBe(["match"]);
        (await Query("""{"metadata":{"u":18000000000000000000}}""")).ShouldBe(["match"]);
        (await Query("""{"metadata":{"u":{"gt":17999999999999999999}}}""")).ShouldBe(["match"]);
        (await Query("""{"metadata":{"i":{"lt":0}}}""")).ShouldBe(["match"]);
        (await Query("""{"metadata":{"d":"0.1"}}""")).ShouldBeEmpty();
    }

    /// <summary>A detached element of <paramref name="json"/>.</summary>
    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static TheoryData<string, string> Rows(IEnumerable<string> values)
    {
        var rows = new TheoryData<string, string>();
        foreach (var path in Paths.Keys)
        {
            foreach (var value in values)
            {
                rows.Add(path, value);
            }
        }

        return rows;
    }
}
