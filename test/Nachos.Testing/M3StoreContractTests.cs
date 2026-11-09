using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Abstractions.Work;
using Shouldly;
using Xunit;

namespace Nachos.Testing;

/// <summary>
/// The M3 append additions (chunks and summary work, <see cref="MessageAppend"/>), <see cref="ISummaryStore"/>, and the
/// chunk side of <see cref="IEmbeddingStore"/>. A provider's test project derives from this class and supplies the store.
/// </summary>
public abstract class M3StoreContractTests : M3ContractTestBase
{
    private static IdempotencyWrite ThrowingWrite(string key) =>
        new(key, new string('a', 64), 201, _ => throw new InvalidOperationException("serializer failed"), TimeSpan.FromMinutes(10));

    private static async Task<IReadOnlyList<MessageRecord>> AppendManyAsync(
        IMemoryStore store, string workspace, string session, int count, SummaryIntent? summary = null) =>
        await store.Messages.AppendAsync(
            workspace,
            session,
            new MessageAppend([.. Enumerable.Range(0, count).Select(i => Msg("bob", $"message {i}"))], Summary: summary),
            Ct);

    /// <summary>Leases and completes every due summary item, returning them in queue order.</summary>
    private static async Task<List<WorkItemRecord>> DrainSummaryItemsAsync(IMemoryStore store)
    {
        var items = new List<WorkItemRecord>();
        while (await AcquireAsync(store, WorkTaskType.Summary) is { } lease)
        {
            items.AddRange(lease.Items);
            await store.WorkQueue.CompleteAsync(lease, Ct);
        }

        return items;
    }

    private static SummaryRecord Summary(MessageRecord through, string content, SummaryKind kind = SummaryKind.Short) =>
        new(kind, content, through.PublicId, through.Seq, content.Length, Start);

    // ---------------------------------------------------------------- chunks

    [Fact]
    public async Task Append_InsertsPendingChunks_Atomically()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await NewSessionAsync(store);

        var appended = await store.Messages.AppendAsync(
            workspace,
            session,
            new MessageAppend([Msg("bob", "first message"), Msg("bob", "second message")], Chunks: [["a0", "a1"], ["b0"]]),
            Ct);

        var pending = (await store.Embeddings.ListPendingAsync(100_000, Ct)).Where(p => p.WorkspaceName == workspace).ToList();
        pending.ShouldBe(
            [
                PendingEmbedding.ForChunk(workspace, appended[0].PublicId, 0, "a0"),
                PendingEmbedding.ForChunk(workspace, appended[0].PublicId, 1, "a1"),
                PendingEmbedding.ForChunk(workspace, appended[1].PublicId, 0, "b0"),
            ],
            ignoreOrder: true);
        var countBefore = await store.Embeddings.CountPendingAsync(Ct);

        await Should.ThrowAsync<InvalidOperationException>(() => store.Messages.AppendAsync(
            workspace,
            session,
            new MessageAppend([Msg("bob", "third message")], ThrowingWrite(Unique("key")), Chunks: [["c0"]]),
            Ct));
        await Should.ThrowAsync<ArgumentException>(() => store.Messages.AppendAsync(
            workspace, session, new MessageAppend([Msg("bob", "fourth message")], Chunks: [["d0"], ["d1"]]), Ct));

        (await store.Embeddings.CountPendingAsync(Ct)).ShouldBe(countBefore);
        (await store.Messages.ListAsync(workspace, session, null, new PageRequest(), Ct)).Total.ShouldBe(2);
    }

    [Fact]
    public async Task ChunkEmbedding_SaveAndMarkFailed_LeaveTheBacklog()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await NewSessionAsync(store);
        await store.Messages.AppendAsync(
            workspace, session, new MessageAppend([Msg("bob", "chunked message")], Chunks: [["c0", "c1"]]), Ct);
        var pending = (await store.Embeddings.ListPendingAsync(100_000, Ct)).Where(p => p.WorkspaceName == workspace).ToList();

        await store.Embeddings.SaveAsync(pending.Single(p => p.ChunkIndex == 0), Vector(0), "test-model", Ct);
        await store.Embeddings.MarkFailedAsync(pending.Single(p => p.ChunkIndex == 1), Ct);

        (await store.Embeddings.ListPendingAsync(100_000, Ct)).ShouldNotContain(p => p.WorkspaceName == workspace);
        await Should.ThrowAsync<ArgumentException>(
            () => store.Embeddings.SaveAsync(pending[0], new float[3], "test-model", Ct));
    }

    [Fact]
    public async Task Unchunked_ListsOnlyMessagesWithoutChunks()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await NewSessionAsync(store);
        var chunked = (await store.Messages.AppendAsync(
            workspace, session, new MessageAppend([Msg("bob", "chunked")], Chunks: [["chunked"]]), Ct)).Single();
        var legacy = await store.Messages.AppendAsync(
            workspace, session, [Msg("bob", "appended before M3"), Msg("bob", " \t\n")], idempotency: null, Ct);

        var unchunked = (await store.Embeddings.ListUnchunkedMessagesAsync(100_000, Ct))
            .Where(m => m.WorkspaceName == workspace)
            .Select(m => m.PublicId)
            .ToList();
        unchunked.ShouldBe([legacy[0].PublicId]);

        var chunk = new MessageChunk(workspace, session, legacy[0].PublicId, 0, "appended before M3");
        await store.Embeddings.AddChunksAsync([chunk], Ct);
        var pendingAfterFirst = await store.Embeddings.CountPendingAsync(Ct);
        await store.Embeddings.AddChunksAsync([chunk, chunk with { MessageId = chunked.PublicId }], Ct);

        (await store.Embeddings.CountPendingAsync(Ct)).ShouldBe(pendingAfterFirst);
        (await store.Embeddings.ListUnchunkedMessagesAsync(100_000, Ct)).ShouldNotContain(m => m.WorkspaceName == workspace);
        await store.Embeddings.AddChunksAsync([chunk with { MessageId = "missing" }], Ct);
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            () => store.Embeddings.AddChunksAsync([chunk with { ChunkIndex = -1 }], Ct));
    }

    // ---------------------------------------------------------------- summary work

    [Theory]
    [InlineData(17, 6, new[] { "short:20" })]
    [InlineData(58, 3, new[] { "short:60", "long:60" })]
    [InlineData(0, 45, new[] { "short:40" })]
    [InlineData(0, 19, new string[0])]
    public async Task Append_SummaryThresholdCrossed_EnqueuesOnePerKind(int prior, int batch, string[] expected)
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await NewSessionAsync(store);
        if (prior > 0)
        {
            await AppendManyAsync(store, workspace, session, prior);
        }

        var appended = await AppendManyAsync(store, workspace, session, batch, new SummaryIntent(true, 20, 60));

        var items = await DrainSummaryItemsAsync(store);
        items.Select(item => SummaryPayload.Read(item.Payload)).Select(work => $"{work.Kind.ToString().ToLowerInvariant()}:{work.ThroughSeq}")
            .ShouldBe(expected);
        foreach (var item in items)
        {
            var throughSeq = SummaryPayload.Read(item.Payload).ThroughSeq;
            item.WorkUnitKey.ShouldBe(WorkUnitKeys.Summary(workspace, session));
            item.TaskType.ShouldBe(WorkTaskType.Summary);
            item.WorkspaceName.ShouldBe(workspace);
            item.SessionName.ShouldBe(session);
            item.TokenCount.ShouldBe(0);
            item.MessageId.ShouldBe(appended.Single(m => m.Seq == throughSeq).PublicId);
        }
    }

    [Fact]
    public async Task Append_SummaryDisabled_EnqueuesNothing()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await NewSessionAsync(store);

        await AppendManyAsync(store, workspace, session, 70, new SummaryIntent(false, 20, 60));
        await AppendManyAsync(store, workspace, session, 70);
        await Should.ThrowAsync<InvalidOperationException>(() => store.Messages.AppendAsync(
            workspace,
            session,
            new MessageAppend([.. Enumerable.Range(0, 30).Select(i => Msg("bob", $"m{i}"))], ThrowingWrite(Unique("key")), Summary: new SummaryIntent(true, 20, 60)),
            Ct));

        (await DrainSummaryItemsAsync(store)).ShouldBeEmpty();
    }

    // ---------------------------------------------------------------- summaries

    [Fact]
    public async Task SaveSummary_OlderCoverage_IsIgnored()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await NewSessionAsync(store);
        var intent = new SummaryIntent(true, 20, 60);
        var messages = (await AppendManyAsync(store, workspace, session, 20, intent)).ToList();
        (await store.Summaries.ListAsync(workspace, session, Ct)).ShouldBeEmpty();

        // The second batch's item waits behind the first lease, so each gets its own lease.
        var first = (await AcquireAsync(store, WorkTaskType.Summary)).ShouldNotBeNull();
        messages.AddRange(await AppendManyAsync(store, workspace, session, 20, intent));
        await store.Summaries.SaveAsync(first, workspace, session, [Summary(messages[39], "through forty")], Ct);
        var second = (await AcquireAsync(store, WorkTaskType.Summary)).ShouldNotBeNull();
        await store.Summaries.SaveAsync(second, workspace, session, [Summary(messages[19], "through twenty")], Ct);

        var stored = (await store.Summaries.GetAsync(workspace, session, SummaryKind.Short, Ct)).ShouldNotBeNull();
        stored.ShouldBe(Summary(messages[39], "through forty"));
        (await store.Summaries.GetAsync(workspace, session, SummaryKind.Long, Ct)).ShouldBeNull();
        (await store.Summaries.ListAsync(workspace, session, Ct)).ShouldBe([stored]);
        var totals = (await store.WorkQueue.GetStatusAsync(workspace, null, null, session, Ct)).Totals;
        totals.Completed.ShouldBe(2);
        totals.Pending.ShouldBe(0);
    }

    [Fact]
    public async Task SaveSummary_NewerCoverage_ReplacesInPlace()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await NewSessionAsync(store);
        var intent = new SummaryIntent(true, 20, 40);
        var messages = (await AppendManyAsync(store, workspace, session, 20, intent)).ToList();
        await store.Summaries.SaveAsync(
            (await AcquireAsync(store, WorkTaskType.Summary)).ShouldNotBeNull(), workspace, session, [Summary(messages[19], "twenty")], Ct);
        messages.AddRange(await AppendManyAsync(store, workspace, session, 20, intent));

        var both = (await AcquireAsync(store, WorkTaskType.Summary)).ShouldNotBeNull();
        both.Items.Select(item => SummaryPayload.Read(item.Payload)).ShouldBe(
            [new SummaryWork(SummaryKind.Short, 40), new SummaryWork(SummaryKind.Long, 40)]);
        await Should.ThrowAsync<ArgumentException>(() => store.Summaries.SaveAsync(
            both, workspace, session, [Summary(messages[39], "a"), Summary(messages[39], "b")], Ct));
        await store.Summaries.SaveAsync(
            both, workspace, session, [Summary(messages[39], "forty"), Summary(messages[39], "long forty", SummaryKind.Long)], Ct);

        (await store.Summaries.ListAsync(workspace, session, Ct))
            .ShouldBe([Summary(messages[39], "forty"), Summary(messages[39], "long forty", SummaryKind.Long)]);
        (await AcquireAsync(store, WorkTaskType.Summary)).ShouldBeNull();
    }

    [Fact]
    public async Task SaveSummary_LeaseLost_WritesNothing()
    {
        var (store, clock) = await NewStoreAsync();
        var (workspace, session) = await NewSessionAsync(store);
        var messages = await AppendManyAsync(store, workspace, session, 20, new SummaryIntent(true, 20, 60));
        var stale = (await AcquireAsync(store, WorkTaskType.Summary)).ShouldNotBeNull();
        clock.Advance(Lease + TimeSpan.FromSeconds(1));
        var current = (await AcquireAsync(store, WorkTaskType.Summary, "worker-2")).ShouldNotBeNull();

        await Should.ThrowAsync<LeaseLostException>(
            () => store.Summaries.SaveAsync(stale, workspace, session, [Summary(messages[19], "stale")], Ct));

        (await store.Summaries.ListAsync(workspace, session, Ct)).ShouldBeEmpty();
        await store.WorkQueue.RenewAsync(current, Lease, Ct);
    }

    [Fact]
    public async Task SaveSummary_UnknownCoverage_NotFound_AndLeaseKept()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await NewSessionAsync(store);
        var messages = await AppendManyAsync(store, workspace, session, 20, new SummaryIntent(true, 20, 60));
        var lease = (await AcquireAsync(store, WorkTaskType.Summary)).ShouldNotBeNull();

        await Should.ThrowAsync<NotFoundException>(
            () => store.Summaries.SaveAsync(lease, workspace, session, [Summary(messages[19], "x") with { CoversThroughSeq = 7 }], Ct));
        await Should.ThrowAsync<NotFoundException>(
            () => store.Summaries.GetAsync(workspace, Unique("missing"), SummaryKind.Short, Ct));

        await store.WorkQueue.RenewAsync(lease, Lease, Ct);
    }
}
