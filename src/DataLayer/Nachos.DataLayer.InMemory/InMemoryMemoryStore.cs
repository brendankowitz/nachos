using Nachos.Abstractions.Stores;
using Nachos.DataLayer.InMemory.Storage;
using Nachos.DataLayer.InMemory.Stores;

namespace Nachos.DataLayer.InMemory;

/// <summary>
/// A non-durable <see cref="IMemoryStore"/> that keeps everything in process memory, for development and tests.
/// </summary>
/// <remarks>
/// <para>
/// One instance, and each of its sub-stores, is safe for concurrent use. Instances share nothing: each starts empty.
/// Every time value comes from the <see cref="TimeProvider"/> given at construction, except a message's caller-supplied
/// <c>CreatedAt</c>.
/// </para>
/// <para>
/// JSON is stored canonically (as if parsed from a request), so C#-built values such as a <see cref="Guid"/> are
/// stored and returned as JSON strings; a value with no JSON form (NaN, Infinity, a string with an unpaired surrogate),
/// a repeated property name, or more than 64 levels of nesting is rejected with
/// <see cref="Abstractions.NachosValidationException"/>.
/// </para>
/// <para>
/// The work queue is a lock-guarded table with the SQL provider's lease and fencing semantics, and vector reads are exact
/// brute-force cosine distance.
/// </para>
/// <para>
/// Each workspace is guarded by a synchronous <see cref="Lock"/>, deliberately not a <see cref="SemaphoreSlim"/>:
/// critical sections are short and purely in-memory, and never span an <c>await</c> of user code. The
/// <see cref="Abstractions.Domain.IdempotencyWrite.SerializeResponse"/> callback is synchronous and runs inside the
/// workspace lock, after the append's rows are staged and before they are committed.
/// </para>
/// <para>
/// An <see cref="Abstractions.Domain.IdempotencyWrite.SerializeResponse"/> callback must be a pure function of the
/// records it receives: it must not call back into the store and has no transactional read guarantee. This store fails
/// fast and deterministically: while the callback runs, every entry point (of every sub-store) called from the
/// execution context running it, including work it hands to <c>Task.Run</c>, throws
/// <see cref="InvalidOperationException"/> before taking any lock, instead of reading, writing or deadlocking. If any
/// such call was attempted, the append throws <see cref="InvalidOperationException"/> even when the callback swallowed
/// the exception, and stores nothing (no messages, peers, memberships, idempotency record, work items or <c>Seq</c> gap). Other
/// callers, concurrent appends included, are unaffected. Suppressing execution-context flow
/// (<see cref="ExecutionContext.SuppressFlow"/>) defeats the guard and is out of contract.
/// </para>
/// </remarks>
public sealed partial class InMemoryMemoryStore : IMemoryStore
{
    private readonly InMemoryState _state;

    public InMemoryMemoryStore(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _state = new InMemoryState(clock);
        Workspaces = new InMemoryWorkspaceStore(_state);
        Peers = new InMemoryPeerStore(_state);
        Sessions = new InMemorySessionStore(_state);
        Messages = new InMemoryMessageStore(_state);
        Grants = new InMemoryGrantStore(_state);
        Idempotency = new InMemoryIdempotencyStore(_state);
        Conclusions = new InMemoryConclusionStore(_state);
        PeerCards = new InMemoryPeerCardStore(_state);
        WorkQueue = new InMemoryWorkQueue(_state);
        Embeddings = new InMemoryEmbeddingStore(_state);
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

    /// <returns>False: nothing this store throws is fixed by retrying.</returns>
    public bool IsTransientFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return false;
    }
}
