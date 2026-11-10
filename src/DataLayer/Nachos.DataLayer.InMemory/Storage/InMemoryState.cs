using Nachos.Abstractions;
using Nachos.Abstractions.Domain;

namespace Nachos.DataLayer.InMemory.Storage;

/// <summary>All data of one <see cref="InMemoryMemoryStore"/>, shared by its sub-stores.</summary>
/// <remarks>
/// <para>
/// Locking: <see cref="Gate"/> guards <see cref="Workspaces"/>, every <see cref="WorkspaceEntry.Record"/>,
/// <see cref="Grants"/> and <see cref="Space"/>. Each <see cref="WorkspaceEntry.Gate"/> guards that workspace's peers, sessions, memberships,
/// messages, chunks, summaries and idempotency records. <see cref="WorkQueueState.Gate"/> guards <see cref="Queue"/>.
/// </para>
/// <para>
/// Lock order: a workspace gate, then the queue gate, then <see cref="Gate"/>; any of them may be skipped, none taken out
/// of order. An append enqueues and a worker commit completes its lease under its workspace gate; a deletion transition
/// or removal also changes the workspace row or the grants. An operation looks its workspace up under <see cref="Gate"/>
/// and releases it before taking the workspace's gate. A workspace removed in between is
/// <see cref="WorkspaceEntry.IsDeleting"/> forever, so a write that reaches the detached entry is rejected.
/// </para>
/// <para>
/// Critical sections are short and synchronous, and stored <c>JsonObject</c>s are only read or cloned inside them,
/// because even reading a <c>JsonObject</c> is not thread-safe (it materializes lazily).
/// </para>
/// </remarks>
internal sealed class InMemoryState(TimeProvider clock)
{
    private long _order;

    public TimeProvider Clock { get; } = clock;

    public Lock Gate { get; } = new();

    public Dictionary<string, WorkspaceEntry> Workspaces { get; } = new(StringComparer.Ordinal);

    public List<GrantRecord> Grants { get; } = [];

    /// <summary>The embedding model stamp, held for the life of the process; guarded by <see cref="Gate"/>.</summary>
    public EmbeddingSpace? Space { get; set; }

    /// <summary>The work items and leases of every workspace.</summary>
    public WorkQueueState Queue { get; } = new();

    /// <summary>Rejects calls made from inside this store's <see cref="IdempotencyWrite.SerializeResponse"/> callbacks.</summary>
    public SerializeResponseGuard SerializeResponseGuard { get; } = new();

    /// <summary>
    /// Runs one public store operation through <see cref="StoreTask"/>, first rejecting a call from inside a
    /// <see cref="IdempotencyWrite.SerializeResponse"/> callback (before the cancellation check, any lock or any state).
    /// Every public entry point goes through here.
    /// </summary>
    public Task<T> Run<T>(Func<T> operation, CancellationToken ct) =>
        SerializeResponseGuard.Reject() is { } rejection ? Task.FromException<T>(rejection) : StoreTask.Run(operation, ct);

    /// <inheritdoc cref="Run{T}(Func{T}, CancellationToken)"/>
    public Task Run(Action operation, CancellationToken ct) =>
        SerializeResponseGuard.Reject() is { } rejection ? Task.FromException(rejection) : StoreTask.Run(operation, ct);

    /// <summary>A monotonically increasing insertion number, the tiebreak for rows with equal <c>CreatedAt</c>.</summary>
    public long NextOrder() => Interlocked.Increment(ref _order);

    /// <summary>The workspace, or null. Takes <see cref="Gate"/>.</summary>
    public WorkspaceEntry? FindWorkspace(string name)
    {
        lock (Gate)
        {
            return Workspaces.GetValueOrDefault(name);
        }
    }

    /// <summary>Every workspace, for operations that span them. Takes <see cref="Gate"/>.</summary>
    public IReadOnlyList<WorkspaceEntry> AllWorkspaces()
    {
        lock (Gate)
        {
            return [.. Workspaces.Values];
        }
    }

    /// <summary>The workspace. Takes <see cref="Gate"/>.</summary>
    /// <exception cref="NotFoundException">The workspace does not exist.</exception>
    public WorkspaceEntry RequireWorkspace(string name) =>
        FindWorkspace(name) ?? throw new NotFoundException($"Workspace '{name}' not found.");
}
