using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Abstractions.Stores;
using Nachos.Testing.Json;
using Shouldly;
using Xunit;

namespace Nachos.Testing;

/// <summary>
/// Behavioural contract every <see cref="IMemoryStore"/> provider must satisfy. A provider's test project derives
/// from this class and supplies the store; the tests are the executable definition of the store interfaces.
/// </summary>
/// <remarks>
/// Every test uses freshly generated workspace names, so tests stay isolated when providers share one database.
/// Time-dependent behaviour is driven through the <see cref="FakeTimeProvider"/> handed to
/// <see cref="CreateStore"/>. Stores created during a test are disposed after it.
/// </remarks>
public abstract class StoreContractTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly ConcurrentBag<IMemoryStore> _stores = [];

    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>Creates a store whose time-based values (including idempotency expiry) come from <paramref name="clock"/>.</summary>
    protected abstract IMemoryStore CreateStore(TimeProvider clock);

    /// <summary>Builds the filter for <c>{"metadata":{key:value}}</c> through the shared parser.</summary>
    private static FilterNode MetadataEquals(ResourceKind kind, string key, string value) =>
        FilterParser.Parse(new JsonObject { ["metadata"] = new JsonObject { [key] = value } }, kind)!;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var store in _stores)
        {
            switch (store)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
    }

    private (IMemoryStore Store, FakeTimeProvider Clock) NewStore()
    {
        var clock = new FakeTimeProvider(Start);
        var store = CreateStore(clock);
        _stores.Add(store);
        return (store, clock);
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static JsonObject Json(string key, JsonNode? value) => new() { [key] = value };

    private static NewMessage Msg(
        string peer, string content, DateTimeOffset? createdAt = null, JsonObject? metadata = null) =>
        new(peer, content, Math.Max(1, content.Length / 4), metadata, createdAt);

    private static IdempotencyWrite Write(string key, string label = "hash", TimeSpan? ttl = null) =>
        new(key, Hash(label), 201, messages => string.Join(",", messages.Select(m => m.PublicId)), ttl ?? TimeSpan.FromMinutes(10));

    /// <summary>Request hashes are 64 lowercase hex characters (SHA-256), which is what providers may store.</summary>
    private static string Hash(string label) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(label)));

    private static async Task<string> NewWorkspaceAsync(IMemoryStore store)
    {
        var workspace = Unique("ws");
        await store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct);
        return workspace;
    }

    private static async Task<(string Workspace, string Session)> NewSessionAsync(IMemoryStore store)
    {
        var workspace = await NewWorkspaceAsync(store);
        var session = Unique("sess");
        await store.Sessions.GetOrCreateAsync(workspace, session, null, null, null, Ct);
        return (workspace, session);
    }

    private static async Task<long> CountMessagesAsync(IMemoryStore store, string workspace, string session) =>
        (await store.Messages.ListAsync(workspace, session, null, new PageRequest(1, 1), Ct)).Total;

    private static Page<string> Names<T>(Page<T> page, Func<T, string> name) =>
        new([.. page.Items.Select(name)], page.Total, page.PageNumber, page.Size, page.Pages);

    private static Dictionary<string, SessionPeerConfig> Peers(params string[] names) =>
        names.ToDictionary(n => n, _ => new SessionPeerConfig(true, true));

    private static Dictionary<string, SessionPeerConfig> Peer(string name, bool observeMe, bool observeOthers) =>
        new() { [name] = new SessionPeerConfig(observeMe, observeOthers) };

    /// <summary>Starts <paramref name="count"/> operations behind one gate so they genuinely overlap.</summary>
    private static async Task<T[]> RunConcurrentlyAsync<T>(int count, Func<int, Task<T>> operation)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, count)
            .Select(i => Task.Run(async () =>
            {
                await gate.Task;
                return await operation(i);
            }))
            .ToArray();
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    // ---------------------------------------------------------------- get-or-create

    [Fact]
    public async Task GetOrCreate_ReturnsSameRecordOnSecondCall()
    {
        var (store, clock) = NewStore();
        var workspace = Unique("ws");

        var first = await store.Workspaces.GetOrCreateAsync(workspace, Json("a", 1), Json("c", true), Ct);
        clock.Advance(TimeSpan.FromHours(1));
        var second = await store.Workspaces.GetOrCreateAsync(workspace, Json("a", 2), null, Ct);

        second.Name.ShouldBe(workspace);
        second.CreatedAt.ShouldBe(first.CreatedAt);
        second.CreatedAt.ShouldBe(Start);
        second.Metadata.ToJsonString().ShouldBe(first.Metadata.ToJsonString());
        second.Configuration.ToJsonString().ShouldBe(first.Configuration.ToJsonString());

        var peer1 = await store.Peers.GetOrCreateAsync(workspace, "p", Json("a", 1), null, Ct);
        var peer2 = await store.Peers.GetOrCreateAsync(workspace, "p", Json("a", 2), null, Ct);
        peer2.Metadata.ToJsonString().ShouldBe(peer1.Metadata.ToJsonString());
        peer2.CreatedAt.ShouldBe(peer1.CreatedAt);

        var session1 = await store.Sessions.GetOrCreateAsync(workspace, "s", Json("a", 1), null, null, Ct);
        var session2 = await store.Sessions.GetOrCreateAsync(workspace, "s", Json("a", 2), null, null, Ct);
        session2.Metadata.ToJsonString().ShouldBe(session1.Metadata.ToJsonString());
        session2.CreatedAt.ShouldBe(session1.CreatedAt);
    }

    [Fact]
    public async Task GetOrCreate_IdsAreCaseSensitive()
    {
        var (store, _) = NewStore();
        var lower = Unique("ws");
        var upper = lower.ToUpperInvariant();
        upper.ShouldNotBe(lower);

        await store.Workspaces.GetOrCreateAsync(lower, null, null, Ct);
        await store.Workspaces.GetOrCreateAsync(upper, null, null, Ct);
        (await store.Workspaces.GetAsync(lower, Ct))!.Name.ShouldBe(lower);
        (await store.Workspaces.GetAsync(upper, Ct))!.Name.ShouldBe(upper);

        await store.Peers.GetOrCreateAsync(lower, "alice", Json("who", "lower"), null, Ct);
        await store.Peers.GetOrCreateAsync(lower, "Alice", Json("who", "upper"), null, Ct);
        (await store.Peers.GetAsync(lower, "alice", Ct))!.Metadata["who"]!.GetValue<string>().ShouldBe("lower");
        (await store.Peers.GetAsync(lower, "Alice", Ct))!.Metadata["who"]!.GetValue<string>().ShouldBe("upper");
        (await store.Peers.ListAsync(lower, PeerKind.All, null, new PageRequest(), Ct)).Total.ShouldBe(2);

        await store.Sessions.GetOrCreateAsync(lower, "sess", null, null, null, Ct);
        await store.Sessions.GetOrCreateAsync(lower, "Sess", null, null, null, Ct);
        (await store.Sessions.ListAsync(lower, null, new PageRequest(), Ct)).Total.ShouldBe(2);
        (await store.Sessions.GetAsync(lower, "SESS", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Message_GetByCaseFoldedPublicId_NotFound()
    {
        var (store, _) = NewStore();
        var (workspace, session) = await NewSessionAsync(store);

        // 20 ids make it overwhelmingly likely that at least one contains a lowercase letter.
        var appended = await store.Messages.AppendAsync(
            workspace, session, [.. Enumerable.Range(0, 20).Select(i => Msg("alice", $"m{i}"))], null, Ct);
        var message = appended.First(m => m.PublicId.Any(char.IsLower));

        (await store.Messages.GetAsync(workspace, session, message.PublicId, Ct))!.PublicId.ShouldBe(message.PublicId);
        (await store.Messages.GetAsync(workspace, session, message.PublicId.ToUpperInvariant(), Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task GetOrCreate_IsIdempotentUnderConcurrency()
    {
        var (store, _) = NewStore();
        var workspace = Unique("ws");
        var tag = Unique("tag");
        JsonObject CallerMetadata(int i) => new() { ["caller"] = i, ["tag"] = tag };

        // Each caller brings different metadata; whichever wins, every caller must see the winner's values.
        var workspaces = await RunConcurrentlyAsync(
            32, i => store.Workspaces.GetOrCreateAsync(workspace, CallerMetadata(i), null, Ct));
        workspaces.Select(w => w.Metadata.ToJsonString()).Distinct().Count().ShouldBe(1);
        workspaces.Select(w => w.CreatedAt).Distinct().Count().ShouldBe(1);
        (await store.Workspaces.GetAsync(workspace, Ct))!.Metadata.ToJsonString()
            .ShouldBe(workspaces[0].Metadata.ToJsonString());
        (await store.Workspaces.ListAsync(MetadataEquals(ResourceKind.Workspace, "tag", tag), new PageRequest(), Ct)).Total.ShouldBe(1);

        var peers = await RunConcurrentlyAsync(
            32, i => store.Peers.GetOrCreateAsync(workspace, "alice", CallerMetadata(i), null, Ct));
        peers.Select(p => p.Metadata.ToJsonString()).Distinct().Count().ShouldBe(1);
        peers.Select(p => p.CreatedAt).Distinct().Count().ShouldBe(1);
        (await store.Peers.GetAsync(workspace, "alice", Ct))!.Metadata.ToJsonString()
            .ShouldBe(peers[0].Metadata.ToJsonString());
        (await store.Peers.ListAsync(workspace, PeerKind.All, MetadataEquals(ResourceKind.Peer, "tag", tag), new PageRequest(), Ct))
            .Total.ShouldBe(1);

        var sessions = await RunConcurrentlyAsync(
            32, i => store.Sessions.GetOrCreateAsync(workspace, "s", CallerMetadata(i), null, null, Ct));
        sessions.Select(s => s.Metadata.ToJsonString()).Distinct().Count().ShouldBe(1);
        sessions.Select(s => s.CreatedAt).Distinct().Count().ShouldBe(1);
        (await store.Sessions.GetAsync(workspace, "s", Ct))!.Metadata.ToJsonString()
            .ShouldBe(sessions[0].Metadata.ToJsonString());
        (await store.Sessions.ListAsync(workspace, MetadataEquals(ResourceKind.Session, "tag", tag), new PageRequest(), Ct)).Total.ShouldBe(1);
    }

    [Fact]
    public async Task GetOrCreatePeer_MissingWorkspace_ThrowsNotFound()
    {
        var (store, _) = NewStore();
        var missing = Unique("missing");

        await Should.ThrowAsync<NotFoundException>(() => store.Peers.GetOrCreateAsync(missing, "alice", null, null, Ct));
        await Should.ThrowAsync<NotFoundException>(
            () => store.Sessions.GetOrCreateAsync(missing, "s", null, null, null, Ct));
    }

    [Fact]
    public async Task Create_NullMetadata_IsEmptyObject()
    {
        var (store, _) = NewStore();
        var workspace = Unique("ws");

        var created = await store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct);
        created.Metadata.ToJsonString().ShouldBe("{}");
        created.Configuration.ToJsonString().ShouldBe("{}");
        var reread = await store.Workspaces.GetAsync(workspace, Ct);
        reread!.Metadata.ToJsonString().ShouldBe("{}");
        reread.Configuration.ToJsonString().ShouldBe("{}");

        var peer = await store.Peers.GetOrCreateAsync(workspace, "p", null, null, Ct);
        peer.Metadata.ToJsonString().ShouldBe("{}");
        peer.Configuration.ToJsonString().ShouldBe("{}");

        var session = await store.Sessions.GetOrCreateAsync(workspace, "s", null, null, null, Ct);
        session.Metadata.ToJsonString().ShouldBe("{}");
        session.Configuration.ToJsonString().ShouldBe("{}");

        var message = (await store.Messages.AppendAsync(workspace, "s", [Msg("p", "hi", metadata: null)], null, Ct))[0];
        message.Metadata.ToJsonString().ShouldBe("{}");
    }

    [Fact]
    public async Task Json_InputAndOutput_AreIsolated()
    {
        var (store, _) = NewStore();
        var workspace = Unique("ws");
        var input = Json("a", 1);

        var created = await store.Workspaces.GetOrCreateAsync(workspace, input, Json("c", 1), Ct);
        input["a"] = 99;
        input["extra"] = true;
        created.Metadata["a"] = 42;
        created.Metadata["mutated"] = true;
        created.Metadata.Parent.ShouldBeNull();

        var reread = await store.Workspaces.GetAsync(workspace, Ct);
        reread!.Metadata.ToJsonString().ShouldBe("{\"a\":1}");
        reread.Metadata.Parent.ShouldBeNull();
        reread.Metadata["a"] = 7;
        (await store.Workspaces.GetAsync(workspace, Ct))!.Metadata.ToJsonString().ShouldBe("{\"a\":1}");

        // Metadata handed to Update and Append is cloned as well.
        var updateInput = Json("u", 1);
        var updated = await store.Workspaces.UpdateAsync(workspace, updateInput, null, Ct);
        updateInput["u"] = 2;
        updated.Metadata["u"] = 3;
        (await store.Workspaces.GetAsync(workspace, Ct))!.Metadata.ToJsonString().ShouldBe("{\"u\":1}");

        await store.Sessions.GetOrCreateAsync(workspace, "s", null, null, null, Ct);
        var messageInput = Json("m", 1);
        var message = (await store.Messages.AppendAsync(workspace, "s", [Msg("p", "hi", metadata: messageInput)], null, Ct))[0];
        messageInput["m"] = 2;
        message.Metadata["m"] = 3;
        message.Metadata.Parent.ShouldBeNull();
        (await store.Messages.GetAsync(workspace, "s", message.PublicId, Ct))!.Metadata.ToJsonString()
            .ShouldBe("{\"m\":1}");
    }

    // ---------------------------------------------------------------- update

    [Fact]
    public async Task Update_NullLeavesFieldUnchanged()
    {
        var (store, _) = NewStore();
        var workspace = Unique("ws");
        await store.Workspaces.GetOrCreateAsync(workspace, Json("a", 1), Json("c", true), Ct);

        // Non-null replaces wholesale: the old keys are gone. Null leaves the other field untouched.
        var metadataOnly = await store.Workspaces.UpdateAsync(workspace, Json("b", 2), null, Ct);
        metadataOnly.Metadata.ToJsonString().ShouldBe("{\"b\":2}");
        metadataOnly.Configuration.ToJsonString().ShouldBe("{\"c\":true}");

        var configurationOnly = await store.Workspaces.UpdateAsync(workspace, null, Json("d", "x"), Ct);
        configurationOnly.Metadata.ToJsonString().ShouldBe("{\"b\":2}");
        configurationOnly.Configuration.ToJsonString().ShouldBe("{\"d\":\"x\"}");

        await store.Peers.GetOrCreateAsync(workspace, "p", Json("a", 1), Json("c", true), Ct);
        var peer = await store.Peers.UpdateAsync(workspace, "p", Json("b", 2), null, Ct);
        peer.Metadata.ToJsonString().ShouldBe("{\"b\":2}");
        peer.Configuration.ToJsonString().ShouldBe("{\"c\":true}");
        peer = await store.Peers.UpdateAsync(workspace, "p", null, Json("d", 1), Ct);
        peer.Metadata.ToJsonString().ShouldBe("{\"b\":2}");
        peer.Configuration.ToJsonString().ShouldBe("{\"d\":1}");

        await store.Sessions.GetOrCreateAsync(workspace, "s", Json("a", 1), Json("c", true), null, Ct);
        var session = await store.Sessions.UpdateAsync(workspace, "s", null, Json("d", "x"), Ct);
        session.Metadata.ToJsonString().ShouldBe("{\"a\":1}");
        session.Configuration.ToJsonString().ShouldBe("{\"d\":\"x\"}");
        session = await store.Sessions.UpdateAsync(workspace, "s", Json("b", 2), null, Ct);
        session.Metadata.ToJsonString().ShouldBe("{\"b\":2}");
        session.Configuration.ToJsonString().ShouldBe("{\"d\":\"x\"}");

        var reread = await store.Sessions.GetAsync(workspace, "s", Ct);
        reread!.Metadata.ToJsonString().ShouldBe(session.Metadata.ToJsonString());
        reread.Configuration.ToJsonString().ShouldBe(session.Configuration.ToJsonString());
    }

    [Fact]
    public async Task Update_Missing_ThrowsNotFound()
    {
        var (store, _) = NewStore();
        var (workspace, session) = await NewSessionAsync(store);
        var missing = Unique("missing");

        await Should.ThrowAsync<NotFoundException>(() => store.Workspaces.UpdateAsync(missing, Json("a", 1), null, Ct));
        await Should.ThrowAsync<NotFoundException>(() => store.Peers.UpdateAsync(workspace, missing, Json("a", 1), null, Ct));
        await Should.ThrowAsync<NotFoundException>(() => store.Sessions.UpdateAsync(workspace, missing, Json("a", 1), null, Ct));
        await Should.ThrowAsync<NotFoundException>(
            () => store.Messages.UpdateMetadataAsync(workspace, session, missing, Json("a", 1), Ct));
    }

    [Fact]
    public async Task UpdateMessageMetadata_ReplacesMetadata_PreservesContentSeqCreatedAt()
    {
        var (store, clock) = NewStore();
        var (workspace, session) = await NewSessionAsync(store);
        var backdated = Start.AddDays(-2);
        var appended = await store.Messages.AppendAsync(
            workspace, session, [Msg("alice", "hello", backdated, Json("old", 1)), Msg("alice", "world")], null, Ct);
        var original = appended[0];
        clock.Advance(TimeSpan.FromHours(1));

        var updated = await store.Messages.UpdateMetadataAsync(workspace, session, original.PublicId, Json("new", 2), Ct);

        updated.Metadata.ToJsonString().ShouldBe("{\"new\":2}");
        updated.Content.ShouldBe("hello");
        updated.Seq.ShouldBe(original.Seq);
        updated.CreatedAt.ShouldBe(backdated);
        updated.TokenCount.ShouldBe(original.TokenCount);
        updated.PeerName.ShouldBe("alice");

        var reread = await store.Messages.GetAsync(workspace, session, original.PublicId, Ct);
        reread.ShouldNotBeNull();
        reread.Metadata.ToJsonString().ShouldBe("{\"new\":2}");
        reread.Content.ShouldBe("hello");
        reread.Seq.ShouldBe(original.Seq);
        reread.CreatedAt.ShouldBe(backdated);
        (await store.Messages.GetAsync(workspace, session, appended[1].PublicId, Ct))!.Metadata.ToJsonString()
            .ShouldBe("{}");
    }

    // ---------------------------------------------------------------- listing

    [Fact]
    public async Task List_FiltersAndPages()
    {
        var (store, clock) = NewStore();
        var workspace = Unique("ws");
        await store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct);

        // Five rows created one second apart; rows 0, 2 and 4 carry k = value.
        var value = Unique("v");
        var workspaceFilter = MetadataEquals(ResourceKind.Workspace, "k", value);
        var peerFilter = MetadataEquals(ResourceKind.Peer, "k", value);
        var sessionFilter = MetadataEquals(ResourceKind.Session, "k", value);
        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            var metadata = Json("k", i % 2 == 0 ? value : "other");
            await store.Workspaces.GetOrCreateAsync($"{workspace}-{i}", metadata, null, Ct);
            await store.Peers.GetOrCreateAsync(workspace, $"p{i}", metadata, null, Ct);
            await store.Sessions.GetOrCreateAsync(workspace, $"s{i}", metadata, null, null, Ct);
        }

        await AssertFilteredPagesAsync(
            async page => Names(await store.Workspaces.ListAsync(workspaceFilter, page, Ct), w => w.Name),
            [$"{workspace}-0", $"{workspace}-2", $"{workspace}-4"]);
        await AssertFilteredPagesAsync(
            async page => Names(await store.Peers.ListAsync(workspace, PeerKind.Regular, peerFilter, page, Ct), p => p.Name),
            ["p0", "p2", "p4"]);
        await AssertFilteredPagesAsync(
            async page => Names(await store.Sessions.ListAsync(workspace, sessionFilter, page, Ct), s => s.Name),
            ["s0", "s2", "s4"]);
    }

    /// <summary>With three matching rows and size 2: 2 pages, ascending order on both, and exact reversal.</summary>
    private static async Task AssertFilteredPagesAsync(Func<PageRequest, Task<Page<string>>> list, string[] ascending)
    {
        ascending.Length.ShouldBe(3);

        var first = await list(new PageRequest(1, 2));
        first.Total.ShouldBe(3);
        first.Pages.ShouldBe(2);
        first.PageNumber.ShouldBe(1);
        first.Size.ShouldBe(2);
        first.Items.ShouldBe([ascending[0], ascending[1]]);

        var second = await list(new PageRequest(2, 2));
        second.Total.ShouldBe(3);
        second.Items.ShouldBe([ascending[2]]);

        var beyond = await list(new PageRequest(3, 2));
        beyond.Total.ShouldBe(3);
        beyond.Items.ShouldBeEmpty();

        (await list(new PageRequest(1, 2, true))).Items.ShouldBe([ascending[2], ascending[1]]);
        (await list(new PageRequest(2, 2, true))).Items.ShouldBe([ascending[0]]);
    }

    [Fact]
    public async Task List_SameCreatedAt_UsesInsertionOrder()
    {
        var (store, _) = NewStore();
        var workspace = Unique("ws");
        var tag = Unique("tag");
        var filter = MetadataEquals(ResourceKind.Workspace, "tag", tag);

        // The clock never moves, so every row has the same CreatedAt and only insertion order can break the tie.
        await store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct);
        string[] names = ["n0", "n1", "n2", "n3", "n4"];
        foreach (var name in names)
        {
            await store.Workspaces.GetOrCreateAsync($"{workspace}-{name}", Json("tag", tag), null, Ct);
            await store.Peers.GetOrCreateAsync(workspace, name, null, null, Ct);
            await store.Sessions.GetOrCreateAsync(workspace, name, null, null, null, Ct);
        }

        var expectedWorkspaces = names.Select(n => $"{workspace}-{n}").ToArray();
        (await store.Workspaces.ListAsync(filter, new PageRequest(), Ct)).Items.Select(w => w.Name)
            .ShouldBe(expectedWorkspaces);
        (await store.Workspaces.ListAsync(filter, new PageRequest(1, 50, true), Ct)).Items.Select(w => w.Name)
            .ShouldBe(expectedWorkspaces.Reverse());

        (await store.Peers.ListAsync(workspace, PeerKind.All, null, new PageRequest(), Ct)).Items.Select(p => p.Name)
            .ShouldBe(names);
        (await store.Peers.ListAsync(workspace, PeerKind.All, null, new PageRequest(1, 50, true), Ct)).Items
            .Select(p => p.Name).ShouldBe(names.Reverse());

        (await store.Sessions.ListAsync(workspace, null, new PageRequest(), Ct)).Items.Select(s => s.Name)
            .ShouldBe(names);
        (await store.Sessions.ListAsync(workspace, null, new PageRequest(1, 50, true), Ct)).Items
            .Select(s => s.Name).ShouldBe(names.Reverse());
    }

    [Fact]
    public async Task ListPeers_KindFiltersInternal()
    {
        var (store, clock) = NewStore();
        var workspace = await NewWorkspaceAsync(store);
        await store.Peers.GetOrCreateAsync(workspace, "regular1", null, null, Ct);
        clock.Advance(TimeSpan.FromSeconds(1));
        await store.Peers.GetOrCreateAsync(workspace, "scope1", null, null, Ct, isInternal: true);
        clock.Advance(TimeSpan.FromSeconds(1));
        await store.Peers.GetOrCreateAsync(workspace, "regular2", null, null, Ct);

        (await store.Peers.GetAsync(workspace, "scope1", Ct))!.IsInternal.ShouldBeTrue();
        (await store.Peers.GetAsync(workspace, "regular1", Ct))!.IsInternal.ShouldBeFalse();

        async Task<string[]> ListAsync(PeerKind kind) =>
            [.. (await store.Peers.ListAsync(workspace, kind, null, new PageRequest(), Ct)).Items.Select(p => p.Name)];

        (await ListAsync(PeerKind.Regular)).ShouldBe(["regular1", "regular2"]);
        (await ListAsync(PeerKind.Scope)).ShouldBe(["scope1"]);
        (await ListAsync(PeerKind.All)).ShouldBe(["regular1", "scope1", "regular2"]);

        // isInternal only applies on create: asking again for an existing peer does not flip it.
        (await store.Peers.GetOrCreateAsync(workspace, "regular1", null, null, Ct, isInternal: true))
            .IsInternal.ShouldBeFalse();
    }

    // ---------------------------------------------------------------- messages

    [Fact]
    public async Task Append_AllocatesContiguousSeqAndJoinsSender()
    {
        var (store, clock) = NewStore();
        var (workspace, session) = await NewSessionAsync(store);
        var backdated = Start.AddDays(-3);

        var first = await store.Messages.AppendAsync(
            workspace,
            session,
            [Msg("alice", "one"), Msg("bob", "two", backdated, Json("m", 1)), Msg("alice", "three")],
            null,
            Ct);
        var second = await store.Messages.AppendAsync(workspace, session, [Msg("bob", "four")], null, Ct);

        var seqs = first.Concat(second).Select(m => m.Seq).ToList();
        seqs.Zip(seqs.Skip(1)).ShouldAllBe(pair => pair.Second == pair.First + 1);

        first.Select(m => m.Content).ShouldBe(["one", "two", "three"]);
        first.ShouldAllBe(m => m.WorkspaceName == workspace && m.SessionName == session);
        first.ShouldAllBe(m => m.PublicId.Length == PublicId.Length);
        first.Select(m => m.PeerName).ShouldBe(["alice", "bob", "alice"]);
        first[0].TokenCount.ShouldBe(Math.Max(1, "one".Length / 4));
        first[0].CreatedAt.ShouldBe(clock.GetUtcNow());
        first[1].CreatedAt.ShouldBe(backdated);
        first[1].Metadata["m"]!.GetValue<int>().ShouldBe(1);

        (await store.Peers.GetAsync(workspace, "alice", Ct)).ShouldNotBeNull();
        (await store.Sessions.IsActiveMemberAsync(workspace, session, "alice", Ct)).ShouldBeTrue();
        (await store.Sessions.IsActiveMemberAsync(workspace, session, "bob", Ct)).ShouldBeTrue();
        (await store.Sessions.IsActiveMemberAsync(workspace, session, "carol", Ct)).ShouldBeFalse();
        var members = await store.Sessions.ListPeersAsync(workspace, session, new PageRequest(), Ct);
        members.Items.Select(p => p.Name).ShouldBe(["alice", "bob"], ignoreOrder: true);
    }

    [Fact]
    public async Task Append_PreservesUnicodeAndMaxLengthContent()
    {
        var (store, _) = NewStore();
        var (workspace, session) = await NewSessionAsync(store);

        // 1562 x 16 UTF-16 units, plus 8 filler characters, is exactly 25,000 without splitting a surrogate pair.
        var unit = "😀𝔘 שלום مرحبا ";
        var content = string.Concat(Enumerable.Repeat(unit, 1562)) + new string('x', 8);
        content.Length.ShouldBe(25_000);
        var metadata = Json("emoji", "😀𝔘 שלום مرحبا");

        var appended = await store.Messages.AppendAsync(
            workspace, session, [Msg("alice", content, metadata: metadata), Msg("alice", string.Empty)], null, Ct);

        var stored = await store.Messages.GetAsync(workspace, session, appended[0].PublicId, Ct);
        stored!.Content.ShouldBe(content);
        Encoding.UTF8.GetBytes(stored.Content).ShouldBe(Encoding.UTF8.GetBytes(content));
        stored.Metadata["emoji"]!.GetValue<string>().ShouldBe("😀𝔘 שלום مرحبا");
        stored.TokenCount.ShouldBe(appended[0].TokenCount);

        var listed = await store.Messages.ListAsync(workspace, session, null, new PageRequest(), Ct);
        listed.Items[0].Content.ShouldBe(content);
        listed.Items[1].Content.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListMessages_OrdersBySeqNotTimestamp()
    {
        var (store, _) = NewStore();
        var (workspace, session) = await NewSessionAsync(store);

        await store.Messages.AppendAsync(
            workspace,
            session,
            [Msg("alice", "m1", Start), Msg("alice", "m2", Start.AddDays(-1)), Msg("alice", "m3", Start)],
            null,
            Ct);

        var ascending = await store.Messages.ListAsync(workspace, session, null, new PageRequest(1, 50, false), Ct);
        var descending = await store.Messages.ListAsync(workspace, session, null, new PageRequest(1, 50, true), Ct);

        ascending.Items.Select(m => m.Content).ShouldBe(["m1", "m2", "m3"]);
        descending.Items.Select(m => m.Content).ShouldBe(["m3", "m2", "m1"]);
    }

    [Fact]
    public async Task Append_MissingSession_ThrowsNotFound()
    {
        var (store, _) = NewStore();
        var workspace = await NewWorkspaceAsync(store);
        var missing = Unique("missing");

        await Should.ThrowAsync<NotFoundException>(
            () => store.Messages.AppendAsync(workspace, missing, [Msg("alice", "hi")], null, Ct));
        // A failed append leaves nothing behind, not even the sender peer.
        (await store.Peers.GetAsync(workspace, "alice", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task SessionOperations_MissingSession_ThrowNotFound()
    {
        var (store, _) = NewStore();
        var workspace = await NewWorkspaceAsync(store);
        var missing = Unique("missing");
        var peers = Peers("alice");
        var page = new PageRequest();

        await Should.ThrowAsync<NotFoundException>(() => store.Sessions.AddPeersAsync(workspace, missing, peers, Ct));
        await Should.ThrowAsync<NotFoundException>(() => store.Sessions.SetPeersAsync(workspace, missing, peers, Ct));
        await Should.ThrowAsync<NotFoundException>(() => store.Sessions.RemovePeersAsync(workspace, missing, ["alice"], Ct));
        await Should.ThrowAsync<NotFoundException>(() => store.Sessions.ListPeersAsync(workspace, missing, page, Ct));
        await Should.ThrowAsync<NotFoundException>(() => store.Sessions.GetPeerConfigAsync(workspace, missing, "alice", Ct));
        await Should.ThrowAsync<NotFoundException>(
            () => store.Sessions.SetPeerConfigAsync(workspace, missing, "alice", new SessionPeerConfig(true, true), Ct));
        await Should.ThrowAsync<NotFoundException>(() => store.Messages.ListAsync(workspace, missing, null, page, Ct));
        await Should.ThrowAsync<NotFoundException>(() => store.Messages.GetAsync(workspace, missing, "anything", Ct));
        await Should.ThrowAsync<NotFoundException>(
            () => store.Messages.UpdateMetadataAsync(workspace, missing, "anything", Json("a", 1), Ct));
    }

    [Fact]
    public async Task Message_MissingInExistingSession_GetIsNullAndUpdateThrows()
    {
        var (store, _) = NewStore();
        var (workspace, session) = await NewSessionAsync(store);

        (await store.Messages.GetAsync(workspace, session, PublicId.New(), Ct)).ShouldBeNull();
        await Should.ThrowAsync<NotFoundException>(
            () => store.Messages.UpdateMetadataAsync(workspace, session, PublicId.New(), Json("a", 1), Ct));
    }

    // ---------------------------------------------------------------- membership

    [Fact]
    public async Task SetPeers_MarksUnlistedLeft()
    {
        var (store, _) = NewStore();
        var workspace = await NewWorkspaceAsync(store);
        await store.Sessions.GetOrCreateAsync(workspace, "s", null, null, Peers("a", "b", "c"), Ct);

        await store.Sessions.SetPeersAsync(
            workspace,
            "s",
            new Dictionary<string, SessionPeerConfig> { ["b"] = new(true, false), ["d"] = new(true, true) },
            Ct);

        (await store.Sessions.IsActiveMemberAsync(workspace, "s", "a", Ct)).ShouldBeFalse();
        (await store.Sessions.IsActiveMemberAsync(workspace, "s", "c", Ct)).ShouldBeFalse();
        (await store.Sessions.IsActiveMemberAsync(workspace, "s", "b", Ct)).ShouldBeTrue();
        (await store.Sessions.IsActiveMemberAsync(workspace, "s", "d", Ct)).ShouldBeTrue();
        (await store.Sessions.GetPeerConfigAsync(workspace, "s", "b", Ct)).ShouldBe(new SessionPeerConfig(true, false));

        await store.Sessions.SetPeerConfigAsync(workspace, "s", "b", new SessionPeerConfig(false, true), Ct);
        (await store.Sessions.GetPeerConfigAsync(workspace, "s", "b", Ct)).ShouldBe(new SessionPeerConfig(false, true));

        var members = await store.Sessions.ListPeersAsync(workspace, "s", new PageRequest(), Ct);
        members.Items.Select(p => p.Name).ShouldBe(["b", "d"], ignoreOrder: true);
    }

    [Fact]
    public async Task RemovePeers_ExcludesFromListPeers()
    {
        var (store, _) = NewStore();
        var (workspace, session) = await NewSessionAsync(store);
        await store.Sessions.AddPeersAsync(workspace, session, Peers("a", "b"), Ct);

        await store.Sessions.RemovePeersAsync(workspace, session, ["a"], Ct);

        var members = await store.Sessions.ListPeersAsync(workspace, session, new PageRequest(), Ct);
        members.Total.ShouldBe(1);
        members.Items.Select(p => p.Name).ShouldBe(["b"]);
        (await store.Sessions.IsActiveMemberAsync(workspace, session, "a", Ct)).ShouldBeFalse();
        (await store.Peers.GetAsync(workspace, "a", Ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task RemovePeers_NonMember_IsNoOp()
    {
        var (store, _) = NewStore();
        var (workspace, session) = await NewSessionAsync(store);
        await store.Sessions.AddPeersAsync(workspace, session, Peers("a", "b"), Ct);

        await store.Sessions.RemovePeersAsync(workspace, session, ["stranger", "a", "a"], Ct);
        await store.Sessions.RemovePeersAsync(workspace, session, ["stranger", "a"], Ct);

        (await store.Sessions.ListPeersAsync(workspace, session, new PageRequest(), Ct)).Items.Select(p => p.Name)
            .ShouldBe(["b"]);
        (await store.Peers.GetAsync(workspace, "stranger", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task GetPeerConfig_NonMember_ThrowsNotFound()
    {
        var (store, _) = NewStore();
        var (workspace, session) = await NewSessionAsync(store);
        await store.Sessions.AddPeersAsync(workspace, session, Peers("left"), Ct);
        await store.Sessions.RemovePeersAsync(workspace, session, ["left"], Ct);
        await store.Peers.GetOrCreateAsync(workspace, "outsider", null, null, Ct);

        foreach (var peer in new[] { "left", "outsider", "unknown" })
        {
            await Should.ThrowAsync<NotFoundException>(() => store.Sessions.GetPeerConfigAsync(workspace, session, peer, Ct));
            await Should.ThrowAsync<NotFoundException>(
                () => store.Sessions.SetPeerConfigAsync(workspace, session, peer, new SessionPeerConfig(true, true), Ct));
        }
    }

    [Fact]
    public async Task RemovedPeer_ReAdded_IsActiveAgain()
    {
        var (store, clock) = NewStore();
        var (workspace, session) = await NewSessionAsync(store);
        await store.Sessions.AddPeersAsync(workspace, session, Peer("a", true, true), Ct);
        await store.Sessions.RemovePeersAsync(workspace, session, ["a"], Ct);
        (await store.Sessions.IsActiveMemberAsync(workspace, session, "a", Ct)).ShouldBeFalse();
        clock.Advance(TimeSpan.FromHours(1));

        // AddPeers reactivates in place with the new config.
        await store.Sessions.AddPeersAsync(workspace, session, Peer("a", false, false), Ct);
        (await store.Sessions.IsActiveMemberAsync(workspace, session, "a", Ct)).ShouldBeTrue();
        (await store.Sessions.GetPeerConfigAsync(workspace, session, "a", Ct)).ShouldBe(new SessionPeerConfig(false, false));
        (await store.Sessions.ListPeersAsync(workspace, session, new PageRequest(), Ct)).Items.Select(p => p.Name)
            .ShouldBe(["a"]);

        // SetPeers reactivates too.
        await store.Sessions.SetPeersAsync(workspace, session, Peers("b"), Ct);
        (await store.Sessions.IsActiveMemberAsync(workspace, session, "a", Ct)).ShouldBeFalse();
        await store.Sessions.SetPeersAsync(workspace, session, Peer("a", true, false), Ct);
        (await store.Sessions.IsActiveMemberAsync(workspace, session, "a", Ct)).ShouldBeTrue();
        (await store.Sessions.GetPeerConfigAsync(workspace, session, "a", Ct)).ShouldBe(new SessionPeerConfig(true, false));

        // So does a message from the former member, and no duplicate row ever appears.
        await store.Sessions.RemovePeersAsync(workspace, session, ["a"], Ct);
        await store.Messages.AppendAsync(workspace, session, [Msg("a", "back")], null, Ct);
        (await store.Sessions.IsActiveMemberAsync(workspace, session, "a", Ct)).ShouldBeTrue();
        (await store.Sessions.ListPeersAsync(workspace, session, new PageRequest(), Ct)).Items.Count(p => p.Name == "a")
            .ShouldBe(1);
    }

    [Fact]
    public async Task GetOrCreateExistingSession_WithPeers_EnsuresMembership()
    {
        var (store, _) = NewStore();
        var (workspace, session) = await NewSessionAsync(store);

        var existing = await store.Sessions.GetOrCreateAsync(
            workspace,
            session,
            Json("ignored", 1),
            null,
            new Dictionary<string, SessionPeerConfig> { ["a"] = new(true, false), ["b"] = new(false, true) },
            Ct);

        existing.Metadata.ToJsonString().ShouldBe("{}");
        (await store.Sessions.IsActiveMemberAsync(workspace, session, "a", Ct)).ShouldBeTrue();
        (await store.Sessions.IsActiveMemberAsync(workspace, session, "b", Ct)).ShouldBeTrue();
        (await store.Sessions.GetPeerConfigAsync(workspace, session, "a", Ct)).ShouldBe(new SessionPeerConfig(true, false));

        // A later call with different config updates it.
        await store.Sessions.GetOrCreateAsync(workspace, session, null, null, Peer("a", false, false), Ct);
        (await store.Sessions.GetPeerConfigAsync(workspace, session, "a", Ct)).ShouldBe(new SessionPeerConfig(false, false));
    }

    [Fact]
    public async Task ListSessionsForPeer_ExcludesLeftSessions()
    {
        var (store, clock) = NewStore();
        var workspace = await NewWorkspaceAsync(store);
        string[] sessions = ["s0", "s1", "s2", "s3"];
        foreach (var name in sessions)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            // s3 never includes alice.
            await store.Sessions.GetOrCreateAsync(
                workspace, name, null, null, name == "s3" ? Peers("bob") : Peers("alice", "bob"), Ct);
        }

        await store.Sessions.RemovePeersAsync(workspace, "s1", ["alice"], Ct);

        var listed = await store.Peers.ListSessionsForPeerAsync(workspace, "alice", null, new PageRequest(), Ct);
        listed.Total.ShouldBe(2);
        listed.Items.Select(s => s.Name).ShouldBe(["s0", "s2"]);
        (await store.Peers.ListSessionsForPeerAsync(workspace, "alice", null, new PageRequest(1, 50, true), Ct)).Items
            .Select(s => s.Name).ShouldBe(["s2", "s0"]);
        (await store.Peers.ListSessionsForPeerAsync(workspace, "bob", null, new PageRequest(), Ct)).Total.ShouldBe(4);
    }

    [Fact]
    public async Task List_MissingWorkspace_ThrowsNotFound()
    {
        var (store, _) = NewStore();
        var missing = Unique("missing");

        await Should.ThrowAsync<NotFoundException>(
            () => store.Peers.ListAsync(missing, PeerKind.All, null, new PageRequest(), Ct));
        await Should.ThrowAsync<NotFoundException>(() => store.Sessions.ListAsync(missing, null, new PageRequest(), Ct));
    }

    [Fact]
    public async Task ListSessionsForPeer_UnknownPeer_ThrowsNotFound()
    {
        var (store, _) = NewStore();
        var workspace = await NewWorkspaceAsync(store);

        await Should.ThrowAsync<NotFoundException>(
            () => store.Peers.ListSessionsForPeerAsync(workspace, Unique("unknown"), null, new PageRequest(), Ct));
    }

    [Fact]
    public async Task IsActiveMember_MissingSession_ThrowsNotFound()
    {
        var (store, _) = NewStore();
        var (workspace, session) = await NewSessionAsync(store);

        await Should.ThrowAsync<NotFoundException>(
            () => store.Sessions.IsActiveMemberAsync(workspace, Unique("missing"), "alice", Ct));
        // An unknown peer in an existing session is simply not a member.
        (await store.Sessions.IsActiveMemberAsync(workspace, session, Unique("unknown"), Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Append_FromLeftPeer_ReactivatesKeepingConfig()
    {
        var (store, _) = NewStore();
        var (workspace, session) = await NewSessionAsync(store);
        await store.Sessions.AddPeersAsync(workspace, session, Peer("a", true, false), Ct);
        await store.Sessions.RemovePeersAsync(workspace, session, ["a"], Ct);
        (await store.Sessions.IsActiveMemberAsync(workspace, session, "a", Ct)).ShouldBeFalse();

        await store.Messages.AppendAsync(workspace, session, [Msg("a", "back")], null, Ct);

        (await store.Sessions.IsActiveMemberAsync(workspace, session, "a", Ct)).ShouldBeTrue();
        (await store.Sessions.GetPeerConfigAsync(workspace, session, "a", Ct)).ShouldBe(new SessionPeerConfig(true, false));
        (await store.Sessions.ListPeersAsync(workspace, session, new PageRequest(), Ct)).Items.Count(p => p.Name == "a")
            .ShouldBe(1);
    }
    // ---------------------------------------------------------------- idempotency

    [Fact]
    public async Task Append_WithIdempotency_StoresRecordAtomically()
    {
        var (store, clock) = NewStore();
        var (workspace, session) = await NewSessionAsync(store);

        var key = Unique("key");
        var stored = await store.Messages.AppendAsync(
            workspace, session, [Msg("alice", "one"), Msg("alice", "two")], Write(key, "h1", TimeSpan.FromMinutes(5)), Ct);
        var record = await store.Idempotency.TryGetAsync(workspace, key, Ct);
        record.ShouldNotBeNull();
        record.RequestHash.ShouldBe(Hash("h1"));
        record.ResponseStatus.ShouldBe(201);
        record.ResponseBody.ShouldBe(string.Join(",", stored.Select(m => m.PublicId)));
        record.ExpiresAt.ShouldBe(clock.GetUtcNow() + TimeSpan.FromMinutes(5));

        // A throwing serializer must roll back everything the attempt did.
        var faultyKey = Unique("key");
        var faulty = new IdempotencyWrite(
            faultyKey, Hash("h2"), 201, _ => throw new InvalidOperationException("boom"), TimeSpan.FromMinutes(5));
        await Should.ThrowAsync<InvalidOperationException>(
            () => store.Messages.AppendAsync(workspace, session, [Msg("bob", "three")], faulty, Ct));

        (await CountMessagesAsync(store, workspace, session)).ShouldBe(2);
        (await store.Idempotency.TryGetAsync(workspace, faultyKey, Ct)).ShouldBeNull();
        (await store.Peers.GetAsync(workspace, "bob", Ct)).ShouldBeNull();
        (await store.Sessions.IsActiveMemberAsync(workspace, session, "bob", Ct)).ShouldBeFalse();

        // No Seq gap: the next append continues exactly after the last committed message.
        var next = await store.Messages.AppendAsync(workspace, session, [Msg("alice", "four")], null, Ct);
        next[0].Seq.ShouldBe(stored[^1].Seq + 1);
    }

    [Fact]
    public async Task Append_SameIdempotencyKeyTwice_SecondThrowsDuplicate()
    {
        var (store, _) = NewStore();
        var (workspace, session) = await NewSessionAsync(store);
        var key = Unique("key");
        await store.Messages.AppendAsync(workspace, session, [Msg("alice", "one")], Write(key, "h1"), Ct);

        var duplicate = await Should.ThrowAsync<IdempotencyDuplicateException>(
            () => store.Messages.AppendAsync(workspace, session, [Msg("alice", "again")], Write(key, "h2"), Ct));

        duplicate.Key.ShouldBe(key);
        (await CountMessagesAsync(store, workspace, session)).ShouldBe(1);
        (await store.Idempotency.TryGetAsync(workspace, key, Ct))!.RequestHash.ShouldBe(Hash("h1"));
    }

    [Fact]
    public async Task Append_ExpiredIdempotencyKey_IsReclaimedAndSucceeds()
    {
        var (store, clock) = NewStore();
        var (workspace, session) = await NewSessionAsync(store);
        var key = Unique("key");
        await store.Messages.AppendAsync(
            workspace, session, [Msg("alice", "one")], Write(key, "old", TimeSpan.FromMinutes(1)), Ct);

        clock.Advance(TimeSpan.FromMinutes(2));
        (await store.Idempotency.TryGetAsync(workspace, key, Ct)).ShouldBeNull();

        var reused = await store.Messages.AppendAsync(
            workspace, session, [Msg("alice", "two")], Write(key, "new", TimeSpan.FromMinutes(1)), Ct);

        reused.Count.ShouldBe(1);
        var record = await store.Idempotency.TryGetAsync(workspace, key, Ct);
        record.ShouldNotBeNull();
        record.RequestHash.ShouldBe(Hash("new"));
        record.ExpiresAt.ShouldBe(clock.GetUtcNow() + TimeSpan.FromMinutes(1));
        (await CountMessagesAsync(store, workspace, session)).ShouldBe(2);
    }

    [Fact]
    public async Task Idempotency_ExpiresExactlyAtTtl()
    {
        var (store, clock) = NewStore();
        var (workspace, session) = await NewSessionAsync(store);
        var ttl = TimeSpan.FromMinutes(1);
        var key = Unique("key");
        await store.Messages.AppendAsync(workspace, session, [Msg("alice", "one")], Write(key, "old", ttl), Ct);

        clock.Advance(ttl - TimeSpan.FromTicks(1));
        (await store.Idempotency.TryGetAsync(workspace, key, Ct)).ShouldNotBeNull();

        // ExpiresAt <= now means expired: at exactly the TTL the record is gone for reads and reclaimable by append.
        clock.Advance(TimeSpan.FromTicks(1));
        (await store.Idempotency.TryGetAsync(workspace, key, Ct)).ShouldBeNull();
        await store.Messages.AppendAsync(workspace, session, [Msg("alice", "two")], Write(key, "new", ttl), Ct);
        (await store.Idempotency.TryGetAsync(workspace, key, Ct))!.RequestHash.ShouldBe(Hash("new"));
    }

    [Fact]
    public async Task Append_ConcurrentReuseOfExpiredKey_OneWinner()
    {
        var (store, clock) = NewStore();
        var (workspace, session) = await NewSessionAsync(store);
        var key = Unique("key");
        await store.Messages.AppendAsync(
            workspace, session, [Msg("alice", "seed")], Write(key, "old", TimeSpan.FromMinutes(1)), Ct);
        clock.Advance(TimeSpan.FromMinutes(2));

        var outcomes = await RaceAppendsAsync(store, workspace, session, key);

        outcomes.Count(e => e is null).ShouldBe(1);
        outcomes.Where(e => e is not null).ShouldAllBe(e => e is IdempotencyDuplicateException);
        // The seed message plus exactly one winning batch of two.
        (await CountMessagesAsync(store, workspace, session)).ShouldBe(3);
        (await store.Idempotency.TryGetAsync(workspace, key, Ct))!.RequestHash.ShouldBeOneOf(RaceHashes);
    }

    [Fact]
    public async Task Append_ConcurrentSameFreshKey_OneWinner()
    {
        var (store, _) = NewStore();
        var (workspace, session) = await NewSessionAsync(store);
        var key = Unique("key");

        var outcomes = await RaceAppendsAsync(store, workspace, session, key);

        outcomes.Count(e => e is null).ShouldBe(1);
        outcomes.Where(e => e is not null).ShouldAllBe(e => e is IdempotencyDuplicateException);
        // Exactly one batch (two messages) was stored, and the record belongs to the winner.
        var messages = (await store.Messages.ListAsync(workspace, session, null, new PageRequest(), Ct)).Items;
        messages.Count.ShouldBe(2);
        messages.Select(m => m.Content.Split(' ')[1]).Distinct().Count().ShouldBe(1);
        (await store.Idempotency.TryGetAsync(workspace, key, Ct))!.RequestHash.ShouldBeOneOf(RaceHashes);
    }

    private static readonly string[] RaceHashes = [.. Enumerable.Range(0, 8).Select(i => Hash($"hash-{i}"))];

    /// <summary>8 parallel two-message appends sharing one idempotency key; each result is null for a success, else the exception.</summary>
    private static Task<Exception?[]> RaceAppendsAsync(IMemoryStore store, string workspace, string session, string key) =>
        RunConcurrentlyAsync<Exception?>(8, async i =>
        {
            try
            {
                await store.Messages.AppendAsync(
                    workspace,
                    session,
                    [Msg("alice", $"racer {i} a"), Msg("alice", $"racer {i} b")],
                    Write(key, $"hash-{i}", TimeSpan.FromMinutes(1)),
                    Ct);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        });

    [Fact]
    public async Task Append_MaxLengthIdempotencyKey_Accepted()
    {
        var (store, _) = NewStore();
        var (workspace, session) = await NewSessionAsync(store);
        var key = new string('k', 255);

        await store.Messages.AppendAsync(workspace, session, [Msg("alice", "one")], Write(key), Ct);

        (await store.Idempotency.TryGetAsync(workspace, key, Ct))!.Key.ShouldBe(key);
    }

    // ---------------------------------------------------------------- grants

    [Fact]
    public async Task Grants_AddListRemove()
    {
        var (store, _) = NewStore();
        var subject = Unique("obj");
        var workspaceA = await NewWorkspaceAsync(store);
        var workspaceB = await NewWorkspaceAsync(store);
        var reader = new GrantRecord(subject, workspaceA, GrantRoles.Workspace);
        var writer = new GrantRecord(subject, workspaceB, GrantRoles.Workspace);
        var admin = new GrantRecord(subject, null, GrantRoles.Admin);
        var other = new GrantRecord(Unique("obj"), workspaceA, GrantRoles.Workspace);

        foreach (var grant in new[] { reader, writer, admin, other })
        {
            await store.Grants.AddAsync(grant, Ct);
        }

        (await store.Grants.ListAsync(subject, Ct)).ShouldBe([reader, writer, admin], ignoreOrder: true);
        (await store.Grants.ListAsync(null, Ct)).ShouldContain(other);
        var grants = await store.Grants.GetWorkspaceGrantsAsync(subject, Ct);
        grants.AllWorkspaces.ShouldBeFalse();
        grants.Workspaces.ShouldBe([workspaceA, workspaceB], ignoreOrder: true);

        await store.Grants.RemoveAsync(reader, Ct);

        (await store.Grants.ListAsync(subject, Ct)).ShouldBe([writer, admin], ignoreOrder: true);
        (await store.Grants.GetWorkspaceGrantsAsync(subject, Ct)).Workspaces.ShouldBe([workspaceB]);
        (await store.Grants.ListAsync(other.ObjectId, Ct)).ShouldBe([other]);
    }

    [Fact]
    public async Task Grants_AddForUnknownWorkspace_ThrowsNotFound()
    {
        var (store, _) = NewStore();
        var subject = Unique("obj");

        await Should.ThrowAsync<NotFoundException>(
            () => store.Grants.AddAsync(new GrantRecord(subject, Unique("missing"), GrantRoles.Workspace), Ct));

        (await store.Grants.ListAsync(subject, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Grants_AddTwice_IsNoOp()
    {
        var (store, _) = NewStore();
        var subject = Unique("obj");
        var workspace = await NewWorkspaceAsync(store);
        var scoped = new GrantRecord(subject, workspace, GrantRoles.Workspace);
        var everywhere = new GrantRecord(subject, null, GrantRoles.Workspace);

        await store.Grants.AddAsync(scoped, Ct);
        await store.Grants.AddAsync(scoped, Ct);
        await store.Grants.AddAsync(everywhere, Ct);
        await store.Grants.AddAsync(everywhere, Ct);

        (await store.Grants.ListAsync(subject, Ct)).ShouldBe([scoped, everywhere], ignoreOrder: true);
    }

    [Fact]
    public async Task Grants_RemoveMissing_IsNoOp()
    {
        var (store, _) = NewStore();
        var subject = Unique("obj");
        var workspace = await NewWorkspaceAsync(store);
        var kept = new GrantRecord(subject, workspace, GrantRoles.Workspace);
        await store.Grants.AddAsync(kept, Ct);

        await store.Grants.RemoveAsync(new GrantRecord(subject, workspace, GrantRoles.Admin), Ct);
        await store.Grants.RemoveAsync(new GrantRecord(subject, null, GrantRoles.Workspace), Ct);
        await store.Grants.RemoveAsync(new GrantRecord(Unique("nobody"), workspace, GrantRoles.Workspace), Ct);

        (await store.Grants.ListAsync(subject, Ct)).ShouldBe([kept]);
    }

    [Fact]
    public async Task Grants_NullWorkspaceMeansAllWorkspaces_AndAdminContributesNothing()
    {
        var (store, _) = NewStore();
        var workspace = await NewWorkspaceAsync(store);
        var everywhere = Unique("obj");
        var adminOnly = Unique("obj");
        var nothing = Unique("obj");
        await store.Grants.AddAsync(new GrantRecord(everywhere, null, GrantRoles.Workspace), Ct);
        await store.Grants.AddAsync(new GrantRecord(everywhere, workspace, GrantRoles.Workspace), Ct);
        await store.Grants.AddAsync(new GrantRecord(adminOnly, null, GrantRoles.Admin), Ct);

        var all = await store.Grants.GetWorkspaceGrantsAsync(everywhere, Ct);
        all.AllWorkspaces.ShouldBeTrue();
        all.Workspaces.ShouldBe([workspace]);

        var admin = await store.Grants.GetWorkspaceGrantsAsync(adminOnly, Ct);
        admin.AllWorkspaces.ShouldBeFalse();
        admin.Workspaces.ShouldBeEmpty();

        var none = await store.Grants.GetWorkspaceGrantsAsync(nothing, Ct);
        none.AllWorkspaces.ShouldBeFalse();
        none.Workspaces.ShouldBeEmpty();
    }

    // ---------------------------------------------------------------- strict JSON data and factory purity

    private const string PendingStrictData =
        "Pending provider adoption: strict JSON data (#6, 3b) — provider owners remove this Skip when their adoption lands";

    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(15);

    private static readonly string[] LoneSurrogateList = ["\uD800"];

    private static readonly int[] OneElementList = [1];

    /// <summary>Typed values a provider must reject: a fresh node each call, because a node has one parent.</summary>
    private static readonly Func<JsonNode>[] TypedCompositeValues =
    [
        () => JsonValue.Create(LoneSurrogateList)!,
        () => JsonValue.Create(new List<int>(OneElementList))!,
        () => JsonValue.Create(DayOfWeek.Monday)!,
    ];

    /// <summary>
    /// Stored metadata is strict JSON data: a value backed by a collection, array or enum is a 422 on create and on
    /// update (see <c>StrictJsonData</c>), and nothing is stored.
    /// </summary>
    [Fact(Skip = PendingStrictData)]
    public async Task StrictData_TypedCompositeMetadata_IsRejected()
    {
        var (store, _) = NewStore();

        foreach (var typed in TypedCompositeValues)
        {
            var created = Unique("ws");
            await Should.ThrowAsync<NachosValidationException>(
                () => store.Workspaces.GetOrCreateAsync(created, Json("k", typed()), null, Ct));
            (await store.Workspaces.GetAsync(created, Ct)).ShouldBeNull();

            var existing = Unique("ws");
            await store.Workspaces.GetOrCreateAsync(existing, Json("keep", 1), null, Ct);
            await Should.ThrowAsync<NachosValidationException>(
                () => store.Workspaces.UpdateAsync(existing, Json("k", typed()), null, Ct));
            (await store.Workspaces.GetAsync(existing, Ct))!.Metadata.ToJsonString().ShouldBe("""{"keep":1}""");
        }
    }

    /// <summary>
    /// A converter attached to a scalar by the caller is never run and never stored: the literal value is.
    /// </summary>
    [Fact(Skip = PendingStrictData)]
    public async Task StrictData_ScalarConverter_NotInvoked()
    {
        var (store, _) = NewStore();
        var converter = new CountingUppercaseConverter();
        var workspace = Unique("ws");

        var created = await store.Workspaces.GetOrCreateAsync(
            workspace, Json("k", StrictJsonSamples.UppercasedString("abc", converter)), null, Ct);
        var reread = await store.Workspaces.GetAsync(workspace, Ct);

        created.Metadata.ToJsonString().ShouldBe("""{"k":"abc"}""");
        reread!.Metadata.ToJsonString().ShouldBe("""{"k":"abc"}""");
        converter.Calls.ShouldBe(0);
    }

    /// <summary>
    /// <see cref="IdempotencyWrite.SerializeResponse"/> must not call back into any store; a provider fails fast with
    /// <see cref="InvalidOperationException"/> and commits nothing. A provider that blocks on re-entry fails these
    /// tests by timing out.
    /// </summary>
    [Fact(Skip = PendingStrictData)]
    public async Task AppendFactory_ReentersStore_ThrowsInvalidOperation()
    {
        var (store, _) = NewStore();
        var (workspace, session) = await NewSessionAsync(store);
        var key = Unique("key");
        var reentrant = new IdempotencyWrite(
            key,
            Hash("reenter"),
            201,
            _ =>
            {
                store.Messages.GetAsync(workspace, session, "missing", Ct).GetAwaiter().GetResult();
                return "body";
            },
            TimeSpan.FromMinutes(10));

        await Should.ThrowAsync<InvalidOperationException>(
            () => store.Messages.AppendAsync(workspace, session, [Msg("alice", "one")], reentrant, Ct).WaitAsync(HangGuard));

        (await CountMessagesAsync(store, workspace, session)).ShouldBe(0);
        (await store.Peers.ListAsync(workspace, PeerKind.All, null, new PageRequest(), Ct)).Total.ShouldBe(0);

        // The failed attempt left no idempotency record: the same key is still free.
        var retried = await store.Messages.AppendAsync(workspace, session, [Msg("alice", "one")], Write(key, "reenter"), Ct);
        retried.Count.ShouldBe(1);
    }

    [Fact(Skip = PendingStrictData)]
    public async Task AppendFactory_CreatesWorkspaceThroughTheStore_ThrowsInvalidOperationAndCreatesNothing()
    {
        var (store, _) = NewStore();
        var (workspace, session) = await NewSessionAsync(store);
        var other = Unique("other");
        var reentrant = new IdempotencyWrite(
            Unique("key"),
            Hash("reenter-write"),
            201,
            _ =>
            {
                store.Workspaces.GetOrCreateAsync(other, null, null, Ct).GetAwaiter().GetResult();
                return "body";
            },
            TimeSpan.FromMinutes(10));

        await Should.ThrowAsync<InvalidOperationException>(
            () => store.Messages.AppendAsync(workspace, session, [Msg("alice", "one")], reentrant, Ct).WaitAsync(HangGuard));

        (await store.Workspaces.GetAsync(other, Ct)).ShouldBeNull();
        (await CountMessagesAsync(store, workspace, session)).ShouldBe(0);
    }
}