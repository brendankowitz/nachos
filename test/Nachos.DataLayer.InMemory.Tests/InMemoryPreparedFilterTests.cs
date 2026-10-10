using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Testing.Json;
using Shouldly;

namespace Nachos.DataLayer.InMemory.Tests;

/// <summary>
/// A filter is prepared once per query: its operands are read once, the prepared form gives exactly the results of the
/// per-row wrapper and of a hand-written expectation, and it is detached from the caller's tree.
/// </summary>
public sealed class InMemoryPreparedFilterTests
{
    private static CancellationToken Ct => CancellationToken.None;

    private static readonly DateTimeOffset Jan1 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Jan2 = Jan1.AddDays(1);
    private static readonly DateTimeOffset Jan3 = Jan1.AddDays(2);

    // ------------------------------------------------------------------------------------------------ node helpers

    private static JsonValue Text(string text) => JsonValue.Create(text);

    private static JsonNode Num(string literal) => JsonNode.Parse(literal)!;

    private static JsonValue Instant(DateTimeOffset at) => JsonValue.Create(at);

    private static JsonArray Items(params JsonNode[] items) => new JsonArray(items);

    private static FilterNode.Field Col(string column, FilterOp op, JsonNode? operand = null) => new FilterNode.Field(column, op, operand);

    private static FilterNode.MetadataPath Meta(string[] path, FilterOp op, JsonNode? operand = null) =>
        new FilterNode.MetadataPath(path, op, operand);

    private static FilterNode.MetadataPath Meta(string key, FilterOp op, JsonNode? operand = null) => Meta([key], op, operand);

    private static FilterNode.And All(params FilterNode[] children) => new(children);

    private static FilterNode.Or Any(params FilterNode[] children) => new(children);

    private static FilterNode.Not None(params FilterNode[] children) => new(children);

    private static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;

    /// <summary>A filter, and the ids of the rows it must select.</summary>
    private sealed record Case(string Name, FilterNode Filter, string[] Expected);

    // ------------------------------------------------------------------------------------------------ data

    private static readonly MessageRecord[] Messages =
    [
        Message("m1", "s1", "alice", "Hello World", 5, Jan1,
            """{"role":"user","score":1.5,"tags":["a","b"],"flag":true,"nested":{"k":"v"},"a.b":"dotted"}"""),
        Message("m2", "s1", "bob", "hello there", 10, Jan2,
            """{"role":"bot","score":2,"tags":["b","c"],"flag":false}"""),
        Message("m3", "s2", "alice", "Goodbye", 0, Jan3, """{"score":"text","tags":"a"}"""),
        Message("m4", "s2", "carol", "ÄPFEL", 7, Jan2, "{}"),
    ];

    private static MessageRecord Message(
        string id, string session, string peer, string content, int tokens, DateTimeOffset at, string metadata) =>
        new(id, "w", session, peer, 1, content, tokens, Obj(metadata), at);

    private static readonly Case[] MessageCases =
    [
        new("id eq", Col(FilterColumns.PublicId, FilterOp.Eq, Text("m1")), ["m1"]),
        new("id ne", Col(FilterColumns.PublicId, FilterOp.Ne, Text("m1")), ["m2", "m3", "m4"]),
        new("session eq", Col(FilterColumns.SessionId, FilterOp.Eq, Text("s2")), ["m3", "m4"]),
        new("peer in", Col(FilterColumns.PeerId, FilterOp.In, Items(Text("alice"), Text("carol"))), ["m1", "m3", "m4"]),
        new("content contains", Col(FilterColumns.Content, FilterOp.Contains, Text("ello")), ["m1", "m2"]),
        new("content contains is case sensitive", Col(FilterColumns.Content, FilterOp.Contains, Text("Hello")), ["m1"]),
        new("content icontains", Col(FilterColumns.Content, FilterOp.IContains, Text("HELLO")), ["m1", "m2"]),
        new("content icontains folds non-ascii", Col(FilterColumns.Content, FilterOp.IContains, Text("äpfel")), ["m4"]),
        new("content gt is ordinal", Col(FilterColumns.Content, FilterOp.Gt, Text("Hello World")), ["m2", "m4"]),
        new("content gte", Col(FilterColumns.Content, FilterOp.Gte, Text("Hello World")), ["m1", "m2", "m4"]),
        new("content lt", Col(FilterColumns.Content, FilterOp.Lt, Text("Hello World")), ["m3"]),
        new("content lte", Col(FilterColumns.Content, FilterOp.Lte, Text("Hello World")), ["m1", "m3"]),
        new("content not null", Col(FilterColumns.Content, FilterOp.NotNull), ["m1", "m2", "m3", "m4"]),
        new("content is null", Col(FilterColumns.Content, FilterOp.IsNull), []),
        new("tokens gte", Col(FilterColumns.TokenCount, FilterOp.Gte, Num("7")), ["m2", "m4"]),
        new("tokens lt", Col(FilterColumns.TokenCount, FilterOp.Lt, Num("5")), ["m3"]),
        new("tokens in", Col(FilterColumns.TokenCount, FilterOp.In, Items(Num("0"), Num("10"))), ["m2", "m3"]),
        new("created eq", Col(FilterColumns.CreatedAt, FilterOp.Eq, Instant(Jan2)), ["m2", "m4"]),
        new("created eq at another offset", Col(FilterColumns.CreatedAt, FilterOp.Eq, Instant(Jan2.ToOffset(TimeSpan.FromHours(1)))), ["m2", "m4"]),
        new("created gt", Col(FilterColumns.CreatedAt, FilterOp.Gt, Instant(Jan1)), ["m2", "m3", "m4"]),
        new("created in", Col(FilterColumns.CreatedAt, FilterOp.In, Items(Instant(Jan1), Instant(Jan3))), ["m1", "m3"]),

        new("meta eq", Meta("role", FilterOp.Eq, Text("user")), ["m1"]),
        new("meta ne includes unset", Meta("role", FilterOp.Ne, Text("user")), ["m2", "m3", "m4"]),
        new("meta is null", Meta("role", FilterOp.IsNull), ["m3", "m4"]),
        new("meta not null", Meta("role", FilterOp.NotNull), ["m1", "m2"]),
        new("meta number gt skips other kinds", Meta("score", FilterOp.Gt, Num("1.5")), ["m2"]),
        new("meta number lte", Meta("score", FilterOp.Lte, Num("1.5")), ["m1"]),
        new("meta number ne includes other kinds", Meta("score", FilterOp.Ne, Num("2")), ["m1", "m3", "m4"]),
        new("meta number eq is by value", Meta("score", FilterOp.Eq, Num("2.0")), ["m2"]),
        new("meta number eq is exact", Meta("score", FilterOp.Eq, Num("1.5000000000000000000000000000001")), []),
        new("meta text gt skips numbers", Meta("score", FilterOp.Gt, Text("a")), ["m3"]),
        new("meta bool eq", Meta("flag", FilterOp.Eq, JsonValue.Create(true)), ["m1"]),
        new("meta bool ne", Meta("flag", FilterOp.Ne, JsonValue.Create(true)), ["m2", "m3", "m4"]),
        new("meta contains string or array element", Meta("tags", FilterOp.Contains, Text("a")), ["m1", "m3"]),
        new("meta contains array element must be equal", Meta("tags", FilterOp.Contains, Text("c")), ["m2"]),
        new("meta contains no substring in array", Meta("tags", FilterOp.Contains, Text("ab")), []),
        new("meta icontains", Meta("tags", FilterOp.IContains, Text("A")), ["m1", "m3"]),
        new("meta json contains all", Meta("tags", FilterOp.JsonContains, Items(Text("a"), Text("b"))), ["m1"]),
        new("meta json contains one", Meta("tags", FilterOp.JsonContains, Items(Text("b"))), ["m1", "m2"]),
        new("meta nested path", Meta(["nested", "k"], FilterOp.Eq, Text("v")), ["m1"]),
        new("meta nested path ne", Meta(["nested", "k"], FilterOp.Ne, Text("v")), ["m2", "m3", "m4"]),
        new("meta nested path unset", Meta(["nested", "k"], FilterOp.IsNull), ["m2", "m3", "m4"]),
        new("meta path through a non-object is unset", Meta(["role", "k"], FilterOp.IsNull), ["m1", "m2", "m3", "m4"]),
        new("meta key with a dot is literal", Meta("a.b", FilterOp.Eq, Text("dotted")), ["m1"]),

        new("and", All(Meta("role", FilterOp.Eq, Text("user")), Col(FilterColumns.TokenCount, FilterOp.Gt, Num("1"))), ["m1"]),
        new("or", Any(Col(FilterColumns.PublicId, FilterOp.Eq, Text("m4")), Meta("role", FilterOp.Eq, Text("bot"))), ["m2", "m4"]),
        new("not is none-of", None(Col(FilterColumns.PeerId, FilterOp.Eq, Text("alice")), Meta("role", FilterOp.Eq, Text("bot"))), ["m4"]),
        new("empty and", All(), ["m1", "m2", "m3", "m4"]),
        new("empty or", Any(), []),
        new("empty not", None(), ["m1", "m2", "m3", "m4"]),
        new("match all", new FilterNode.MatchAll(), ["m1", "m2", "m3", "m4"]),
        new("match none", new FilterNode.MatchNone(), []),
        new(
            "nested",
            All(
                Any(Col(FilterColumns.SessionId, FilterOp.Eq, Text("s1")), Meta("tags", FilterOp.Contains, Text("a"))),
                None(Any(Col(FilterColumns.PeerId, FilterOp.Eq, Text("bob")), new FilterNode.MatchNone()))),
            ["m1", "m3"]),
        new("or short-circuits before an unknown column", Any(new FilterNode.MatchAll(), Col(FilterColumns.IsActive, FilterOp.Eq, JsonValue.Create(true))), ["m1", "m2", "m3", "m4"]),
    ];

    private static readonly WorkspaceRecord[] Workspaces =
    [
        new("alpha", Obj("""{"tier":"gold","n":1}"""), new JsonObject(), LifecycleState.Active, Jan1),
        new("beta", Obj("""{"tier":"silver","n":2}"""), new JsonObject(), LifecycleState.Active, Jan2),
        new("gamma", Obj("{}"), new JsonObject(), LifecycleState.Active, Jan3),
    ];

    private static readonly Case[] WorkspaceCases =
    [
        new("name eq", Col(FilterColumns.Name, FilterOp.Eq, Text("beta")), ["beta"]),
        new("name ne", Col(FilterColumns.Name, FilterOp.Ne, Text("beta")), ["alpha", "gamma"]),
        new("name in", Col(FilterColumns.Name, FilterOp.In, Items(Text("alpha"), Text("gamma"), Text("zeta"))), ["alpha", "gamma"]),
        new("name contains", Col(FilterColumns.Name, FilterOp.Contains, Text("mm")), ["gamma"]),
        new("name gt", Col(FilterColumns.Name, FilterOp.Gt, Text("alpha")), ["beta", "gamma"]),
        new("created lte", Col(FilterColumns.CreatedAt, FilterOp.Lte, Instant(Jan2)), ["alpha", "beta"]),
        new("meta tier ne", Meta("tier", FilterOp.Ne, Text("gold")), ["beta", "gamma"]),
        new("meta n gte", Meta("n", FilterOp.Gte, Num("1")), ["alpha", "beta"]),
        new("or", Any(Meta("tier", FilterOp.IsNull), Col(FilterColumns.Name, FilterOp.Eq, Text("alpha"))), ["alpha", "gamma"]),
    ];

    private static readonly PeerRecord[] Peers =
    [
        new("w", "alice", Obj("""{"team":"red"}"""), new JsonObject(), false, Jan1),
        new("w", "bob", Obj("""{"team":"blue"}"""), new JsonObject(), false, Jan2),
        new("w", "carol", Obj("{}"), new JsonObject(), true, Jan3),
    ];

    private static readonly Case[] PeerCases =
    [
        new("name icontains", Col(FilterColumns.Name, FilterOp.IContains, Text("AR")), ["carol"]),
        new("created gt", Col(FilterColumns.CreatedAt, FilterOp.Gt, Instant(Jan1)), ["bob", "carol"]),
        new("meta team ne", Meta("team", FilterOp.Ne, Text("red")), ["bob", "carol"]),
        new("meta team eq or name", Any(Meta("team", FilterOp.Eq, Text("blue")), Col(FilterColumns.Name, FilterOp.Eq, Text("carol"))), ["bob", "carol"]),
        new("not", None(Meta("team", FilterOp.NotNull)), ["carol"]),
    ];

    private static readonly SessionRecord[] Sessions =
    [
        new("w", "s1", LifecycleState.Active, Obj("""{"topic":"x"}"""), new JsonObject(), Jan1),
        new("w", "s2", LifecycleState.Inactive, Obj("{}"), new JsonObject(), Jan2),
        new("w", "s3", LifecycleState.Active, Obj("""{"topic":"y"}"""), new JsonObject(), Jan3),
    ];

    /// <summary>The active members of each session (carol left s1, so she is not listed there).</summary>
    private static readonly Dictionary<string, string[]> ActiveMembers = new()
    {
        ["s1"] = ["alice", "bob"],
        ["s2"] = [],
        ["s3"] = ["carol"],
    };

    private static readonly Case[] SessionCases =
    [
        new("member eq", Col(FilterColumns.PeerId, FilterOp.Eq, Text("alice")), ["s1"]),
        new("former member does not count", Col(FilterColumns.PeerId, FilterOp.Eq, Text("zed")), []),
        new("member ne is not eq", Col(FilterColumns.PeerId, FilterOp.Ne, Text("alice")), ["s2", "s3"]),
        new("no members", Col(FilterColumns.PeerId, FilterOp.IsNull), ["s2"]),
        new("has members", Col(FilterColumns.PeerId, FilterOp.NotNull), ["s1", "s3"]),
        new("member in", Col(FilterColumns.PeerId, FilterOp.In, Items(Text("bob"), Text("carol"))), ["s1", "s3"]),
        new("member contains", Col(FilterColumns.PeerId, FilterOp.Contains, Text("o")), ["s1", "s3"]),
        new("member icontains", Col(FilterColumns.PeerId, FilterOp.IContains, Text("ALI")), ["s1"]),
        new("member gt is existential", Col(FilterColumns.PeerId, FilterOp.Gt, Text("bob")), ["s3"]),
        new("is active", Col(FilterColumns.IsActive, FilterOp.Eq, JsonValue.Create(true)), ["s1", "s3"]),
        new("is not active", Col(FilterColumns.IsActive, FilterOp.Ne, JsonValue.Create(true)), ["s2"]),
        new("not member", None(Col(FilterColumns.PeerId, FilterOp.Eq, Text("bob"))), ["s2", "s3"]),
        new("meta and member", All(Meta("topic", FilterOp.Eq, Text("y")), Col(FilterColumns.PeerId, FilterOp.NotNull)), ["s3"]),
        new("created lt", Col(FilterColumns.CreatedAt, FilterOp.Lt, Instant(Jan3)), ["s1", "s2"]),
    ];

    private static string[] Select<T>(IEnumerable<T> rows, Func<T, bool> matches, Func<T, string> id) =>
        [.. rows.Where(matches).Select(id).Order(StringComparer.Ordinal)];

    private static void ShouldSelect<T>(Case[] cases, IEnumerable<T> rows, Func<FilterNode?, T, bool> wrapper,
        Func<PreparedFilter?, T, bool> prepared, Func<T, string> id)
    {
        var all = rows.ToArray();
        foreach (var testCase in cases)
        {
            var expected = testCase.Expected.Order(StringComparer.Ordinal).ToArray();
            var viaPrepared = InMemoryFilterEvaluator.Prepare(testCase.Filter);

            Select(all, row => prepared(viaPrepared, row), id).ShouldBe(expected, $"prepared: {testCase.Name}");
            Select(all, row => wrapper(testCase.Filter, row), id).ShouldBe(expected, $"wrapper: {testCase.Name}");
        }
    }

    // ------------------------------------------------------------------------------------------------ equivalence

    [Fact]
    public void Messages_PreparedWrapperAndExpectedAgree() =>
        ShouldSelect(
            MessageCases,
            Messages,
            InMemoryFilterEvaluator.Matches,
            InMemoryFilterEvaluator.Matches,
            message => message.PublicId);

    [Fact]
    public void Workspaces_PreparedWrapperAndExpectedAgree() =>
        ShouldSelect(
            WorkspaceCases,
            Workspaces,
            InMemoryFilterEvaluator.Matches,
            InMemoryFilterEvaluator.Matches,
            workspace => workspace.Name);

    [Fact]
    public void Peers_PreparedWrapperAndExpectedAgree() =>
        ShouldSelect(
            PeerCases,
            Peers,
            InMemoryFilterEvaluator.Matches,
            InMemoryFilterEvaluator.Matches,
            peer => peer.Name);

    [Fact]
    public void Sessions_PreparedWrapperAndExpectedAgree() =>
        ShouldSelect(
            SessionCases,
            Sessions,
            (filter, session) => InMemoryFilterEvaluator.Matches(filter, session, ActiveMembers[session.Name]),
            (filter, session) => InMemoryFilterEvaluator.Matches(filter, session, ActiveMembers[session.Name]),
            session => session.Name);

    [Fact]
    public void NullFilter_MatchesEveryRow_AndPreparesToNull()
    {
        InMemoryFilterEvaluator.Prepare(null).ShouldBeNull();
        Messages.All(message => InMemoryFilterEvaluator.Matches((PreparedFilter?)null, message)).ShouldBeTrue();
        Messages.All(message => InMemoryFilterEvaluator.Matches((FilterNode?)null, message)).ShouldBeTrue();
    }

    // ------------------------------------------------------------------------------------------------ one read

    private static FilterNode.And ManyLeaves(int items) => All(
        Col(FilterColumns.PublicId, FilterOp.In, new JsonArray([.. Enumerable.Range(0, items).Select(i => (JsonNode)Text("x" + i))])),
        Any(Meta("role", FilterOp.Eq, Text("user")), None(Col(FilterColumns.TokenCount, FilterOp.Gt, Num("3")))),
        Meta(["nested", "k"], FilterOp.IsNull),
        new FilterNode.MatchAll());

    [Fact]
    public void Prepare_ReadsEachLeafOperandOnce_NoMatterHowManyRowsAreMatched()
    {
        var prepared = InMemoryFilterEvaluator.Prepare(ManyLeaves(1000)).ShouldNotBeNull();
        prepared.OperandReads.ShouldBe(4);

        for (var i = 0; i < 500; i++)
        {
            foreach (var message in Messages)
            {
                InMemoryFilterEvaluator.Matches(prepared, message);
            }
        }

        prepared.OperandReads.ShouldBe(4);
    }

    private sealed class PrepareLog : IDisposable
    {
        public PrepareLog() => PreparedFilter.Observer.Value = Prepared.Add;

        public List<PreparedFilter> Prepared { get; } = [];

        public void Dispose() => PreparedFilter.Observer.Value = null;
    }

    private const int RowCount = 150;

    private static InMemoryMemoryStore RowStore()
    {
        var store = new InMemoryMemoryStore(TimeProvider.System);
        store.SeedWorkspace("w", Jan1, new JsonObject());
        for (var i = 0; i < RowCount; i++)
        {
            store.SeedWorkspace("w" + i, Jan1.AddMinutes(i), Obj("""{"role":"user"}"""));
            store.SeedPeer("w", "p" + i, Jan1.AddMinutes(i), Obj("""{"role":"user"}"""));
            store.SeedSession("w", "s" + i, Jan1.AddMinutes(i), isActive: true, Obj("""{"role":"user"}"""));
            store.SeedMember("w", "s" + i, "p" + i, active: true);
            if (i != 0)
            {
                store.SeedMember("w", "s" + i, "p0", active: true);
            }
        }

        for (var i = 0; i < RowCount; i++)
        {
            store.SeedMessage("w", "s0", "m" + i, "p0", "c" + i, i, Jan1.AddMinutes(i), Obj("""{"role":"user"}"""));
        }

        return store;
    }

    private static FilterNode.And ListFilter(string column) =>
        All(
            Col(column, FilterOp.In, new JsonArray([.. Enumerable.Range(0, 1000).Select(i => (JsonNode)Text("x" + i))])),
            Meta("role", FilterOp.Eq, Text("user")));

    [Fact]
    public async Task EveryStoreListCall_PreparesTheFilterOnce_NotOncePerRow()
    {
        var store = RowStore();
        var page = new PageRequest(1, PageRequest.MaxSize);
        var byColumn = Any(Col(FilterColumns.Name, FilterOp.Contains, Text("1")), ListFilter(FilterColumns.Name));
        var messageFilter = Any(Col(FilterColumns.PublicId, FilterOp.Contains, Text("1")), ListFilter(FilterColumns.PublicId));
        var sessionFilter = Any(Col(FilterColumns.PeerId, FilterOp.Contains, Text("1")), ListFilter(FilterColumns.PeerId));

        var calls = new (string Name, FilterNode Filter, Func<FilterNode, Task<long>> List)[]
        {
            ("workspaces", byColumn, async f => (await store.Workspaces.ListAsync(f, page, Ct)).Total),
            ("peers", byColumn, async f => (await store.Peers.ListAsync("w", PeerKind.All, f, page, Ct)).Total),
            ("sessions", sessionFilter, async f => (await store.Sessions.ListAsync("w", f, page, Ct)).Total),
            ("sessions of a peer", sessionFilter, async f => (await store.Peers.ListSessionsForPeerAsync("w", "p0", f, page, Ct)).Total),
            ("messages", messageFilter, async f => (await store.Messages.ListAsync("w", "s0", f, page, Ct)).Total),
        };

        foreach (var (name, filter, list) in calls)
        {
            using var log = new PrepareLog();

            var total = await list(filter);

            total.ShouldBeGreaterThan(1, name);
            log.Prepared.Count.ShouldBe(1, name);
            log.Prepared[0].OperandReads.ShouldBe(3, name); // contains, in, metadata eq
        }
    }

    // ------------------------------------------------------------------------------------------------ timing

    [Fact]
    public void UnknownColumn_ThrowsWhenARowReachesIt_NotWhenPreparing()
    {
        var prepared = InMemoryFilterEvaluator.Prepare(Col(FilterColumns.PeerId, FilterOp.Eq, Text("alice"))).ShouldNotBeNull();

        InMemoryFilterEvaluator.Matches(prepared, Messages[0]).ShouldBeTrue();
        Should.Throw<NotSupportedException>(() => InMemoryFilterEvaluator.Matches(prepared, Workspaces[0]));
        Should.Throw<NotSupportedException>(() => InMemoryFilterEvaluator.Matches(prepared, Peers[0]));
    }

    [Fact]
    public async Task UnknownColumn_ThrowsOnTheFirstRow_AndNeverOnAnEmptyRowSet()
    {
        var store = new InMemoryMemoryStore(TimeProvider.System);
        var filter = Col(FilterColumns.PeerId, FilterOp.Eq, Text("alice"));

        (await store.Workspaces.ListAsync(filter, new PageRequest(), Ct)).Items.ShouldBeEmpty();

        store.SeedWorkspace("w", Jan1, new JsonObject());
        await Should.ThrowAsync<NotSupportedException>(() => store.Workspaces.ListAsync(filter, new PageRequest(), Ct));
        await Should.ThrowAsync<NotFoundException>(() => store.Peers.ListAsync("missing", PeerKind.All, filter, new PageRequest(), Ct));
    }

    [Fact]
    public void ANullChildOfAHandBuiltCombinator_FailsWhenARowReachesIt()
    {
        var prepared = InMemoryFilterEvaluator.Prepare(Any(new FilterNode.MatchAll(), null!)).ShouldNotBeNull();
        InMemoryFilterEvaluator.Matches(prepared, Messages[0]).ShouldBeTrue();

        var reached = InMemoryFilterEvaluator.Prepare(Any(new FilterNode.MatchNone(), null!)).ShouldNotBeNull();
        Should.Throw<ArgumentOutOfRangeException>(() => InMemoryFilterEvaluator.Matches(reached, Messages[0]));
    }

    // ------------------------------------------------------------------------------------------------ one filter, every kind

    [Fact]
    public void OnePreparedFilter_ServesEveryRecordKind()
    {
        var filter = All(
            Col(FilterColumns.CreatedAt, FilterOp.Gte, Instant(Jan2)),
            None(Col(FilterColumns.CreatedAt, FilterOp.Gt, Instant(Jan2))));
        var prepared = InMemoryFilterEvaluator.Prepare(filter);

        InMemoryFilterEvaluator.Matches(prepared, Workspaces[1]).ShouldBeTrue();
        InMemoryFilterEvaluator.Matches(prepared, Workspaces[0]).ShouldBeFalse();
        InMemoryFilterEvaluator.Matches(prepared, Peers[1]).ShouldBeTrue();
        InMemoryFilterEvaluator.Matches(prepared, Peers[2]).ShouldBeFalse();
        InMemoryFilterEvaluator.Matches(prepared, Sessions[1], []).ShouldBeTrue();
        InMemoryFilterEvaluator.Matches(prepared, Sessions[0], []).ShouldBeFalse();
        InMemoryFilterEvaluator.Matches(prepared, Messages[1]).ShouldBeTrue();
        InMemoryFilterEvaluator.Matches(prepared, Messages[2]).ShouldBeFalse();
        InMemoryFilterEvaluator.Matches(prepared, Workspaces[1]).ShouldBeTrue();
    }

    [Fact]
    public void OnePreparedFilter_ResolvesPeerIdPerKind()
    {
        var prepared = InMemoryFilterEvaluator.Prepare(Col(FilterColumns.PeerId, FilterOp.Eq, Text("bob")));

        // A message column, then a session's existential column, then a message again.
        InMemoryFilterEvaluator.Matches(prepared, Messages[1]).ShouldBeTrue();
        InMemoryFilterEvaluator.Matches(prepared, Sessions[0], ["alice", "bob"]).ShouldBeTrue();
        InMemoryFilterEvaluator.Matches(prepared, Sessions[2], ["carol"]).ShouldBeFalse();
        InMemoryFilterEvaluator.Matches(prepared, Messages[0]).ShouldBeFalse();
        InMemoryFilterEvaluator.Matches(prepared, Sessions[1], []).ShouldBeFalse();
    }

    // ------------------------------------------------------------------------------------------------ detached

    [Fact]
    public void Prepared_IsDetachedFromAChildrenListTheCallerKeeps()
    {
        var all = new List<FilterNode> { Meta("role", FilterOp.Eq, Text("user")) };
        var anyOf = new List<FilterNode> { new FilterNode.MatchNone() };
        var noneOf = new List<FilterNode> { Meta("role", FilterOp.Eq, Text("bot")) };
        var preparedAnd = InMemoryFilterEvaluator.Prepare(new FilterNode.And(all));
        var preparedOr = InMemoryFilterEvaluator.Prepare(new FilterNode.Or(anyOf));
        var preparedNot = InMemoryFilterEvaluator.Prepare(new FilterNode.Not(noneOf));

        all.Clear();
        anyOf.Clear();
        noneOf.Clear();
        anyOf.Add(new FilterNode.MatchAll());

        InMemoryFilterEvaluator.Matches(preparedAnd, Messages[1]).ShouldBeFalse(); // role is bot: still needs role = user
        InMemoryFilterEvaluator.Matches(preparedOr, Messages[1]).ShouldBeFalse(); // still only MatchNone
        InMemoryFilterEvaluator.Matches(preparedNot, Messages[1]).ShouldBeFalse(); // role is bot: still excluded
    }

    [Fact]
    public void Prepared_IsDetachedFromAPathListTheCallerKeeps()
    {
        var path = new List<string> { "nested", "k" };
        var prepared = InMemoryFilterEvaluator.Prepare(new FilterNode.MetadataPath(path, FilterOp.Eq, Text("v")));

        path.Clear();
        path.Add("role");

        InMemoryFilterEvaluator.Matches(prepared, Messages[0]).ShouldBeTrue();
        InMemoryFilterEvaluator.Matches(prepared, Messages[1]).ShouldBeFalse();
    }

    [Fact]
    public void Prepared_IsDetachedFromAnOperandTheCallerKeptAHandleOn()
    {
        var operand = Items(Text("m1"), Text("m2"));
        var prepared = InMemoryFilterEvaluator.Prepare(Col(FilterColumns.PublicId, FilterOp.In, operand));

        operand.AsArray().Clear();

        Select(Messages, message => InMemoryFilterEvaluator.Matches(prepared, message), message => message.PublicId)
            .ShouldBe(["m1", "m2"]);
    }

    // ------------------------------------------------------------------------------------------------ converters

    [Fact]
    public void HandBuiltOperandWithAConverter_NeverRunsIt_ThroughThePreparedPath()
    {
        var converter = new CountingUppercaseConverter();
        var filter = All(
            Col(FilterColumns.Content, FilterOp.Eq, StrictJsonSamples.UppercasedString("Goodbye", converter)),
            Meta("score", FilterOp.Eq, StrictJsonSamples.UppercasedString("text", converter)),
            Meta("tags", FilterOp.Contains, StrictJsonSamples.UppercasedString("a", converter)));

        var prepared = InMemoryFilterEvaluator.Prepare(filter);
        var matched = Messages.Where(message => InMemoryFilterEvaluator.Matches(prepared, message)).Select(m => m.PublicId);
        var viaWrapper = Messages.Where(message => InMemoryFilterEvaluator.Matches(filter, message)).Select(m => m.PublicId);

        matched.ShouldBe(["m3"]);
        viaWrapper.ShouldBe(["m3"]);
        converter.Calls.ShouldBe(0);
    }
}
