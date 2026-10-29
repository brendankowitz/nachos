using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Abstractions.Stores;
using Nachos.DataLayer.SqlServer.Schema;
using Nachos.DataLayer.SqlServer.Storage;
using Nachos.DataLayer.SqlServer.Stores;

namespace Nachos.DataLayer.SqlServer;

/// <summary>An <see cref="IMemoryStore"/> over the dacpac-managed SQL Server schema.</summary>
/// <remarks>
/// <para>
/// <b>Lifetime and threading.</b> Registered scoped by <c>UseSqlServer</c>, but safe for concurrent use: every operation
/// opens its own context and connection (pooled). Before its first database access, each operation awaits
/// <see cref="SchemaGate.EnsureAsync"/>, which verifies the schema once per process and remembers the success.
/// </para>
/// <para>
/// <b>Rules.</b> Times come from the <see cref="TimeProvider"/>, never the database clock. Names and public ids
/// compare ordinally and case-sensitively, including trailing spaces. Lists are in creation order with the identity
/// column as the tiebreak (messages in <c>Seq</c> order). Writes that race (get-or-create, membership, <c>Seq</c>,
/// idempotency keys) follow the lock order documented on <c>Storage.Upserts</c>; RCSI makes plain reads lock-free.
/// </para>
/// <para>
/// <b>JSON</b> is stored losslessly as text in <c>nvarchar(max)</c> columns that a CHECK constraint requires to hold a
/// JSON object. Caller JSON passes through <c>StrictJsonData.ToCanonical</c> at every entry point and its canonical text
/// is stored exactly as emitted: numbers keep their spelling (<c>1e2</c>, <c>1E400</c>, 40-digit integers), with no
/// normalization and no size cap. The one limit SQL Server itself imposes is that a key longer than 4000 UTF-16 code
/// units is rejected with <see cref="Abstractions.NachosValidationException"/>, because <c>OPENJSON</c> truncates
/// longer keys (see <c>Storage.SqlJson</c>). Every returned object is freshly parsed.
/// </para>
/// <para>
/// <b>Serializer re-entry.</b> While an <see cref="Abstractions.Domain.IdempotencyWrite.SerializeResponse"/> callback
/// runs inside an append's transaction, every entry point of every <see cref="SqlMemoryStore"/> in the process called
/// from its execution context throws <see cref="InvalidOperationException"/> before touching the database, and the append
/// rolls back. Any store, not just this one: another instance would block on the locks the append holds.
/// </para>
/// </remarks>
public sealed class SqlMemoryStore : IMemoryStore
{
    internal const string PendingM2Message = "The SQL Server M2 store lands in M2 Task 3.";

    public SqlMemoryStore(SqlServerOptions options, SchemaGate schemaGate, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(schemaGate);
        ArgumentNullException.ThrowIfNull(clock);

        var runtime = new SqlStoreRuntime(options, schemaGate, clock);
        Workspaces = new SqlWorkspaceStore(runtime);
        Peers = new SqlPeerStore(runtime);
        Sessions = new SqlSessionStore(runtime);
        Messages = new SqlMessageStore(runtime);
        Grants = new SqlGrantStore(runtime);
        Idempotency = new SqlIdempotencyStore(runtime);
        var pendingM2 = new PendingM2Stores();
        Conclusions = pendingM2;
        PeerCards = pendingM2;
        WorkQueue = pendingM2;
        Embeddings = pendingM2;
    }

    public IWorkspaceStore Workspaces { get; }

    public IPeerStore Peers { get; }

    public ISessionStore Sessions { get; }

    public IMessageStore Messages { get; }

    public IGrantStore Grants { get; }

    public IIdempotencyStore Idempotency { get; }

    public IConclusionStore Conclusions { get; }

    public IPeerCardStore PeerCards { get; }

    public IWorkQueue WorkQueue { get; }

    public IEmbeddingStore Embeddings { get; }

    /// <summary>Temporary placeholder until M2 Task 3: every member throws <see cref="NotSupportedException"/>.</summary>
    private sealed class PendingM2Stores : IConclusionStore, IPeerCardStore, IWorkQueue, IEmbeddingStore
    {
        private static NotSupportedException Pending() => new(PendingM2Message);

        public Task<CollectionRecord?> GetCollectionAsync(string workspaceName, string observerName, string observedName, CancellationToken ct) => throw Pending();

        public Task<ConclusionRecord?> FindExactAsync(string workspaceName, string observerName, string observedName, ConclusionLevel level, string content, string? restrictToSession, CancellationToken ct) => throw Pending();

        public Task<ConclusionMatch?> FindNearestAsync(string workspaceName, string observerName, string observedName, ConclusionLevel level, ReadOnlyMemory<float> vector, double maxDistance, string? restrictToSession, CancellationToken ct) => throw Pending();

        public Task<Page<ConclusionRecord>> ListAsync(string workspaceName, FilterNode? filter, ConclusionOrder order, PageRequest page, CancellationToken ct) => throw Pending();

        public Task<IReadOnlyList<ConclusionMatch>> QueryAsync(string workspaceName, ReadOnlyMemory<float> vector, int topK, double? maxDistance, FilterNode? filter, CancellationToken ct) => throw Pending();

        public Task<ConclusionRecord?> GetAsync(string workspaceName, string publicId, CancellationToken ct) => throw Pending();

        public Task CommitDerivationAsync(WorkLease lease, DerivationChanges changes, CancellationToken ct) => throw Pending();

        public Task<IReadOnlyList<ConclusionRecord>> InsertAsync(string workspaceName, IReadOnlyList<NewConclusion> conclusions, CancellationToken ct) => throw Pending();

        public Task TombstoneAsync(string workspaceName, string publicId, CancellationToken ct) => throw Pending();

        public Task<int> PurgeTombstonesAsync(TimeSpan retention, CancellationToken ct) => throw Pending();

        public Task<PeerCardRecord?> GetAsync(string workspaceName, string observerName, string observedName, CancellationToken ct) => throw Pending();

        public Task<PeerCardRecord> SetAsync(string workspaceName, string observerName, string observedName, IReadOnlyList<string> entries, PeerCardAuthor updatedBy, CancellationToken ct) => throw Pending();

        public Task<WorkLease?> TryAcquireAsync(string owner, IReadOnlyDictionary<WorkTaskType, WorkBatchPolicy> taskTypes, TimeSpan leaseDuration, CancellationToken ct) => throw Pending();

        public Task<WorkLease?> TryLeaseAsync(string workUnitKey, string owner, TimeSpan leaseDuration, CancellationToken ct) => throw Pending();

        public Task RenewAsync(WorkLease lease, TimeSpan leaseDuration, CancellationToken ct) => throw Pending();

        public Task CompleteAsync(WorkLease lease, CancellationToken ct) => throw Pending();

        public Task FailAsync(WorkLease lease, string reason, TimeSpan retryDelay, int maxAttempts, CancellationToken ct) => throw Pending();

        public Task<QueueStatusRecord> GetStatusAsync(string workspaceName, string? observerName, string? senderName, string? sessionName, CancellationToken ct) => throw Pending();

        public Task<int> PruneSucceededAsync(TimeSpan retention, CancellationToken ct) => throw Pending();

        public Task<IReadOnlyList<PendingEmbedding>> ListPendingAsync(int limit, CancellationToken ct) => throw Pending();

        public Task SaveAsync(PendingEmbedding pending, ReadOnlyMemory<float> embedding, CancellationToken ct) => throw Pending();

        public Task MarkFailedAsync(PendingEmbedding pending, CancellationToken ct) => throw Pending();
    }
}
