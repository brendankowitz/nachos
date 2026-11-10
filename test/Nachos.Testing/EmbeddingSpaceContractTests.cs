using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Shouldly;
using Xunit;

namespace Nachos.Testing;

/// <summary>
/// The embedding model stamp (<see cref="IEmbeddingStore.GetSpaceAsync"/> and its siblings). The stamp and a reset are
/// database-wide, so a provider must give every test a store of its own (<see cref="M3ContractTestBase.CreateStore"/>).
/// </summary>
public abstract class EmbeddingSpaceContractTests : M3ContractTestBase
{
    private static readonly EmbeddingSpace Bge = new("bge-small-en-v1.5", 384);
    private static readonly EmbeddingSpace Azure = new("text-embedding-3-small", VectorSpace.Dimensions);

    /// <summary>A session with an embedded conclusion recorded as <paramref name="model"/>, an embedded chunk and a failed chunk.</summary>
    private static async Task<(string Workspace, string Session)> SeedVectorsAsync(IMemoryStore store, string model)
    {
        var (workspace, session) = await NewSessionAsync(store);
        await store.Sessions.AddPeersAsync(workspace, session, Members("alice", "bob"), Ct);
        await store.Conclusions.InsertAsync(
            workspace,
            [
                new NewConclusion("alice", "bob", session, ConclusionLevel.Explicit, "bob likes tea", Vector(1), [],
                    new JsonObject { [VectorSpace.ModelMetadataKey] = model }),
                new NewConclusion("alice", "bob", session, ConclusionLevel.Explicit, "bob likes rain", null, [], null),
            ],
            Ct);
        await store.Messages.AppendAsync(
            workspace, session, new MessageAppend([Msg("bob", "a chunked message")], Chunks: [["c0", "c1"]]), Ct);
        var chunks = (await store.Embeddings.ListPendingAsync(100, Ct)).Where(p => p.Kind == PendingEmbeddingKind.MessageChunk).ToList();
        await store.Embeddings.SaveAsync(chunks.Single(p => p.ChunkIndex == 0), Vector(2), model, Ct);
        await store.Embeddings.MarkFailedAsync(chunks.Single(p => p.ChunkIndex == 1), Ct);
        return (workspace, session);
    }

    [Fact]
    public async Task EmbeddingSpace_Unstamped_IsNull_AndTheFirstStampWins()
    {
        var (store, _) = await NewStoreAsync();

        (await store.Embeddings.GetSpaceAsync(Ct)).ShouldBeNull();
        (await store.Embeddings.StampSpaceAsync(Bge, Ct)).ShouldBe(Bge);
        (await store.Embeddings.StampSpaceAsync(Azure, Ct)).ShouldBe(Bge);
        (await store.Embeddings.GetSpaceAsync(Ct)).ShouldBe(Bge);
    }

    [Fact]
    public async Task EmbeddingSpace_ConcurrentStamps_AgreeOnOne()
    {
        var (store, _) = await NewStoreAsync();

        var stamps = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            store.Embeddings.StampSpaceAsync(new EmbeddingSpace($"model-{i}", 384), Ct)));

        stamps.Distinct().Count().ShouldBe(1);
        (await store.Embeddings.GetSpaceAsync(Ct)).ShouldBe(stamps[0]);
    }

    [Fact]
    public async Task EmbeddingSpace_NoVectors_ListsNoModels()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await NewSessionAsync(store);
        await store.Messages.AppendAsync(workspace, session, new MessageAppend([Msg("bob", "pending only")], Chunks: [["c0"]]), Ct);

        (await store.Embeddings.ListVectorModelsAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task EmbeddingSpace_ListsRecordedModels_AndNullForChunkVectors()
    {
        var (store, _) = await NewStoreAsync();
        await SeedVectorsAsync(store, "fake");

        (await store.Embeddings.ListVectorModelsAsync(Ct)).ShouldBe(["fake", null], ignoreOrder: true);
    }

    [Fact]
    public async Task EmbeddingSpace_Reset_MarksAllPendingAndRestamps()
    {
        var (store, _) = await NewStoreAsync();
        await store.Embeddings.StampSpaceAsync(Azure, Ct);
        var (workspace, _) = await SeedVectorsAsync(store, Azure.Model);
        var tombstoned = await store.Conclusions.InsertAsync(
            workspace, [new NewConclusion("alice", "bob", null, ConclusionLevel.Explicit, "gone", Vector(3), [], null)], Ct);
        await store.Conclusions.TombstoneAsync(workspace, tombstoned[0].PublicId, Ct);

        // An embedded and a tombstoned conclusion are reset, and both chunks deleted; batches of one exercise the loops.
        var reset = await store.Embeddings.ResetSpaceAsync(Bge, batchSize: 1, Ct);

        reset.ShouldBe(4);
        (await store.Embeddings.GetSpaceAsync(Ct)).ShouldBe(Bge);
        (await store.Embeddings.ListVectorModelsAsync(Ct)).ShouldBeEmpty();
        (await store.Embeddings.CountPendingAsync(PendingEmbeddingKind.Conclusion, Ct)).ShouldBe(2);
        (await store.Embeddings.CountPendingAsync(PendingEmbeddingKind.MessageChunk, Ct)).ShouldBe(0);
        var conclusion = (await store.Conclusions.ListAsync(workspace, null, ConclusionOrder.CreatedAt, new PageRequest(), Ct)).Items
            .Single(c => c.Content == "bob likes tea");
        conclusion.InternalMetadata.ContainsKey(VectorSpace.ModelMetadataKey).ShouldBeFalse();

        (await store.Embeddings.ResetSpaceAsync(Bge, batchSize: 100, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task EmbeddingSpace_Reset_DeletesChunks_SoTheirMessagesAreRechunked()
    {
        var (store, _) = await NewStoreAsync();
        var (workspace, session) = await SeedVectorsAsync(store, Azure.Model);
        var message = (await store.Messages.ListAsync(workspace, session, null, new PageRequest(), Ct)).Items.Single();
        (await store.Embeddings.ListUnchunkedMessagesAsync(100, Ct)).ShouldNotContain(m => m.PublicId == message.PublicId);

        await store.Embeddings.ResetSpaceAsync(Bge, batchSize: 1, Ct);

        (await store.Embeddings.ListPendingAsync(100, Ct)).ShouldNotContain(p => p.Kind == PendingEmbeddingKind.MessageChunk);
        (await store.Embeddings.ListUnchunkedMessagesAsync(100, Ct)).Select(m => m.PublicId).ShouldBe([message.PublicId]);
        await store.Embeddings.AddChunksAsync([new MessageChunk(workspace, session, message.PublicId, 0, "rechunked")], Ct);
        (await store.Embeddings.ListPendingAsync(100, Ct)).ShouldContain(p => p.Kind == PendingEmbeddingKind.MessageChunk && p.Content == "rechunked");
    }
}
