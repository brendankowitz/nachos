using Nachos.Abstractions.Stores;
using Nachos.DataLayer.InMemory.Storage;
using Nachos.DataLayer.InMemory.Stores;

namespace Nachos.DataLayer.InMemory;

/// <summary>
/// A non-durable <see cref="IMemoryStore"/> that keeps everything in process memory, for development and tests.
/// </summary>
/// <remarks>
/// One instance, and each of its sub-stores, is safe for concurrent use. Instances share nothing: each starts empty.
/// Every time value comes from the <see cref="TimeProvider"/> given at construction, except a message's caller-supplied
/// <c>CreatedAt</c>.
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
    }

    public IWorkspaceStore Workspaces { get; }

    public IPeerStore Peers { get; }

    public ISessionStore Sessions { get; }

    public IMessageStore Messages { get; }

    public IGrantStore Grants { get; }

    public IIdempotencyStore Idempotency { get; }
}
