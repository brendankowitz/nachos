using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Abstractions.Stores;
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
/// <see cref="CreateStore"/>.
/// </remarks>
public abstract class StoreContractTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>Creates a store whose time-based values (including idempotency expiry) come from <paramref name="clock"/>.</summary>
    protected abstract IMemoryStore CreateStore(TimeProvider clock);

    /// <summary>Builds the filter equivalent to <c>{"metadata":{key:value}}</c>. Replaced by the parser once it exists.</summary>
    protected abstract FilterNode MetadataEquals(string key, string value);

    private (IMemoryStore Store, FakeTimeProvider Clock) NewStore()
    {
        var clock = new FakeTimeProvider(Start);
        return (CreateStore(clock), clock);
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static JsonObject Json(string key, JsonNode? value) => new() { [key] = value };

    private static NewMessage Msg(
        string peer, string content, DateTimeOffset? createdAt = null, JsonObject? metadata = null) =>
        new(peer, content, Math.Max(1, content.Length / 4), metadata, createdAt);

    private static IdempotencyWrite Write(string key, string hash = "hash", TimeSpan? ttl = null) =>
        new(key, hash, 201, messages => string.Join(",", messages.Select(m => m.PublicId)), ttl ?? TimeSpan.FromMinutes(10));

    private static async Task<(string Workspace, string Session)> NewSessionAsync(IMemoryStore store)
    {
        var workspace = Unique("ws");
        var session = Unique("sess");
        await store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct);
        await store.Sessions.GetOrCreateAsync(workspace, session, null, null, null, Ct);
        return (workspace, session);
    }

    private static async Task<long> CountMessagesAsync(IMemoryStore store, string workspace, string session) =>
        (await store.Messages.ListAsync(workspace, session, null, new PageRequest(1, 1), Ct)).Total;

    private static Page<string> Names<T>(Page<T> page, Func<T, string> name) =>
        new([.. page.Items.Select(name)], page.Total, page.PageNumber, page.Size, page.Pages);

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
        (await store.Peers.ListAsync(lower, null, PeerKind.All, new PageRequest(), Ct)).Total.ShouldBe(2);

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

        // 20 ids make it overwhelmingly likely that at least one contains a letter whose case differs.
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

        var workspaces = await RunConcurrentlyAsync(32, _ => store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct));
        workspaces.Select(w => w.Name).Distinct().ShouldBe([workspace]);
        workspaces.Select(w => w.CreatedAt).Distinct().Count().ShouldBe(1);

        var peers = await RunConcurrentlyAsync(32, _ => store.Peers.GetOrCreateAsync(workspace, "alice", null, null, Ct));
        peers.Select(p => p.CreatedAt).Distinct().Count().ShouldBe(1);
        (await store.Peers.ListAsync(workspace, null, PeerKind.All, new PageRequest(), Ct)).Total.ShouldBe(1);

        var sessions = await RunConcurrentlyAsync(
            32, _ => store.Sessions.GetOrCreateAsync(workspace, "s", null, null, null, Ct));
        sessions.Select(s => s.CreatedAt).Distinct().Count().ShouldBe(1);
        (await store.Sessions.ListAsync(workspace, null, new PageRequest(), Ct)).Total.ShouldBe(1);
    }

    [Fact]
    public async Task Update_NullLeavesFieldUnchanged()
    {
        var (store, _) = NewStore();
        var workspace = Unique("ws");
        await store.Workspaces.GetOrCreateAsync(workspace, Json("a", 1), Json("c", true), Ct);

        var metadataOnly = await store.Workspaces.UpdateAsync(workspace, Json("b", 2), null, Ct);
        metadataOnly.Metadata["b"]!.GetValue<int>().ShouldBe(2);
        metadataOnly.Configuration["c"]!.GetValue<bool>().ShouldBeTrue();

        var configurationOnly = await store.Workspaces.UpdateAsync(workspace, null, Json("d", "x"), Ct);
        configurationOnly.Metadata["b"]!.GetValue<int>().ShouldBe(2);
        configurationOnly.Configuration["d"]!.GetValue<string>().ShouldBe("x");

        await store.Peers.GetOrCreateAsync(workspace, "p", Json("a", 1), Json("c", true), Ct);
        var peer = await store.Peers.UpdateAsync(workspace, "p", Json("b", 2), null, Ct);
        peer.Metadata["b"]!.GetValue<int>().ShouldBe(2);
        peer.Configuration["c"]!.GetValue<bool>().ShouldBeTrue();

        await store.Sessions.GetOrCreateAsync(workspace, "s", Json("a", 1), Json("c", true), null, Ct);
        var session = await store.Sessions.UpdateAsync(workspace, "s", null, Json("d", "x"), Ct);
        session.Metadata["a"]!.GetValue<int>().ShouldBe(1);
        session.Configuration["d"]!.GetValue<string>().ShouldBe("x");

        var reread = await store.Sessions.GetAsync(workspace, "s", Ct);
        reread!.Metadata.ToJsonString().ShouldBe(session.Metadata.ToJsonString());
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
    public async Task List_FiltersAndPages()
    {
        var (store, _) = NewStore();
        var workspace = Unique("ws");
        await store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct);

        // Five rows, three of which carry k = v.
        var value = Unique("v");
        var filter = MetadataEquals("k", value);
        for (var i = 0; i < 5; i++)
        {
            var metadata = Json("k", i % 2 == 0 ? value : "other");
            await store.Workspaces.GetOrCreateAsync($"{workspace}-{i}", metadata, null, Ct);
            await store.Peers.GetOrCreateAsync(workspace, $"p{i}", metadata, null, Ct);
            await store.Sessions.GetOrCreateAsync(workspace, $"s{i}", metadata, null, null, Ct);
        }

        await AssertFilteredPagesAsync(
            async page => Names(await store.Workspaces.ListAsync(filter, page, Ct), w => w.Name),
            [$"{workspace}-0", $"{workspace}-2", $"{workspace}-4"]);
        await AssertFilteredPagesAsync(
            async page => Names(await store.Peers.ListAsync(workspace, filter, PeerKind.Regular, page, Ct), p => p.Name),
            ["p0", "p2", "p4"]);
        await AssertFilteredPagesAsync(
            async page => Names(await store.Sessions.ListAsync(workspace, filter, page, Ct), s => s.Name),
            ["s0", "s2", "s4"]);
    }

    private static async Task AssertFilteredPagesAsync(Func<PageRequest, Task<Page<string>>> list, string[] expected)
    {
        var first = await list(new PageRequest(1, 2));
        first.Total.ShouldBe(3);
        first.Pages.ShouldBe(2);
        first.PageNumber.ShouldBe(1);
        first.Size.ShouldBe(2);
        first.Items.Count.ShouldBe(2);

        var second = await list(new PageRequest(2, 2));
        second.Total.ShouldBe(3);
        second.Items.Count.ShouldBe(1);

        var beyond = await list(new PageRequest(3, 2));
        beyond.Total.ShouldBe(3);
        beyond.Items.ShouldBeEmpty();

        first.Items.Concat(second.Items).ShouldBe(expected, ignoreOrder: true);
    }

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
    public async Task SetPeers_MarksUnlistedLeft()
    {
        var (store, _) = NewStore();
        var workspace = Unique("ws");
        await store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct);
        var open = new SessionPeerConfig(true, true);
        await store.Sessions.GetOrCreateAsync(
            workspace, "s", null, null, new Dictionary<string, SessionPeerConfig> { ["a"] = open, ["b"] = open, ["c"] = open }, Ct);

        await store.Sessions.SetPeersAsync(
            workspace,
            "s",
            new Dictionary<string, SessionPeerConfig> { ["b"] = new(true, false), ["d"] = open },
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
        var open = new SessionPeerConfig(true, true);
        await store.Sessions.AddPeersAsync(
            workspace, session, new Dictionary<string, SessionPeerConfig> { ["a"] = open, ["b"] = open }, Ct);

        await store.Sessions.RemovePeersAsync(workspace, session, ["a"], Ct);

        var members = await store.Sessions.ListPeersAsync(workspace, session, new PageRequest(), Ct);
        members.Total.ShouldBe(1);
        members.Items.Select(p => p.Name).ShouldBe(["b"]);
        (await store.Sessions.IsActiveMemberAsync(workspace, session, "a", Ct)).ShouldBeFalse();
        (await store.Peers.GetAsync(workspace, "a", Ct)).ShouldNotBeNull();
    }

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
        record.RequestHash.ShouldBe("h1");
        record.ResponseStatus.ShouldBe(201);
        record.ResponseBody.ShouldBe(string.Join(",", stored.Select(m => m.PublicId)));
        record.ExpiresAt.ShouldBe(clock.GetUtcNow() + TimeSpan.FromMinutes(5));

        // A throwing serializer must roll back both the messages and the record.
        var faultyKey = Unique("key");
        var faulty = new IdempotencyWrite(
            faultyKey, "h2", 201, _ => throw new InvalidOperationException("boom"), TimeSpan.FromMinutes(5));
        await Should.ThrowAsync<InvalidOperationException>(
            () => store.Messages.AppendAsync(workspace, session, [Msg("bob", "three")], faulty, Ct));

        (await CountMessagesAsync(store, workspace, session)).ShouldBe(2);
        (await store.Idempotency.TryGetAsync(workspace, faultyKey, Ct)).ShouldBeNull();
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
        (await store.Idempotency.TryGetAsync(workspace, key, Ct))!.RequestHash.ShouldBe("h1");
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
        record.RequestHash.ShouldBe("new");
        record.ExpiresAt.ShouldBe(clock.GetUtcNow() + TimeSpan.FromMinutes(1));
        (await CountMessagesAsync(store, workspace, session)).ShouldBe(2);
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

        var outcomes = await RunConcurrentlyAsync(8, async i =>
        {
            try
            {
                await store.Messages.AppendAsync(
                    workspace, session, [Msg("alice", $"racer {i}")], Write(key, $"hash-{i}", TimeSpan.FromMinutes(1)), Ct);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        });

        outcomes.Count(e => e is null).ShouldBe(1);
        outcomes.Where(e => e is not null).ShouldAllBe(e => e is IdempotencyDuplicateException);
        (await CountMessagesAsync(store, workspace, session)).ShouldBe(2);
        (await store.Idempotency.TryGetAsync(workspace, key, Ct))!.RequestHash.ShouldStartWith("hash-");
    }

    [Fact]
    public async Task Append_MaxLengthIdempotencyKey_Accepted()
    {
        var (store, _) = NewStore();
        var (workspace, session) = await NewSessionAsync(store);
        var key = new string('k', 255);

        await store.Messages.AppendAsync(workspace, session, [Msg("alice", "one")], Write(key), Ct);

        (await store.Idempotency.TryGetAsync(workspace, key, Ct))!.Key.ShouldBe(key);
    }

    [Fact]
    public async Task Grants_AddListRemove()
    {
        var (store, _) = NewStore();
        var subject = Unique("obj");
        var workspaceA = Unique("ws");
        var workspaceB = Unique("ws");
        var reader = new GrantRecord(subject, workspaceA, "reader");
        var writer = new GrantRecord(subject, workspaceB, "writer");
        var admin = new GrantRecord(subject, null, "admin");
        var other = new GrantRecord(Unique("obj"), workspaceA, "reader");

        foreach (var grant in new[] { reader, writer, admin, other })
        {
            await store.Grants.AddAsync(grant, Ct);
        }

        (await store.Grants.ListAsync(subject, Ct)).ShouldBe([reader, writer, admin], ignoreOrder: true);
        (await store.Grants.ListAsync(null, Ct)).ShouldContain(other);
        (await store.Grants.GetWorkspacesAsync(subject, Ct)).ShouldBe([workspaceA, workspaceB], ignoreOrder: true);

        await store.Grants.RemoveAsync(reader, Ct);

        (await store.Grants.ListAsync(subject, Ct)).ShouldBe([writer, admin], ignoreOrder: true);
        (await store.Grants.GetWorkspacesAsync(subject, Ct)).ShouldBe([workspaceB]);
        (await store.Grants.ListAsync(other.ObjectId, Ct)).ShouldBe([other]);
    }

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
}