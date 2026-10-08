namespace Nachos.Abstractions.Stores;

/// <summary>
/// The storage boundary. Every provider must honour these rules, which <c>StoreContractTests</c> pins.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>
/// <b>Clock:</b> providers take a <see cref="TimeProvider"/> and compute every time-based value
/// (<c>CreatedAt</c>, <c>JoinedAt</c>, <c>LeftAt</c>, idempotency expiry) from it, never from a database clock.
/// </description></item>
/// <item><description>
/// <b>Case sensitivity:</b> workspace, peer and session names and message public ids compare case-sensitively
/// and ordinally.
/// </description></item>
/// <item><description>
/// <b>Thread safety:</b> one instance, and each of its sub-stores, must be safe for concurrent use by many callers.
/// An implementation therefore cannot share one non-thread-safe <c>DbContext</c> across calls; it uses a context
/// factory or a connection per operation.
/// </description></item>
/// <item><description>
/// <b>JSON isolation:</b> caller-supplied <see cref="System.Text.Json.Nodes.JsonObject"/> arguments pass through
/// <see cref="Nachos.Abstractions.Json.StrictJsonData.ToCanonical"/> on input, so a value outside the strict JSON-data
/// contract is rejected with <see cref="NachosValidationException"/> and nothing is stored; only the detached canonical
/// copy is kept. Every returned <see cref="System.Text.Json.Nodes.JsonObject"/> is a fresh deep clone (no parent), so
/// callers and stores never share instances. Null metadata or configuration on create is stored and returned as <c>{}</c>.
/// </description></item>
/// <item><description>
/// <b>Ordering:</b> workspace, peer and session lists are ordered by creation (<c>CreatedAt</c>, with insertion
/// order as the tiebreak) and <see cref="PageRequest.Reverse"/> flips that order. Message lists are ordered by
/// <c>Seq</c>.
/// </description></item>
/// </list>
/// </remarks>
public interface IMemoryStore
{
    /// <summary>Workspace storage.</summary>
    IWorkspaceStore Workspaces { get; }

    /// <summary>Peer storage.</summary>
    IPeerStore Peers { get; }

    /// <summary>Session and membership storage.</summary>
    ISessionStore Sessions { get; }

    /// <summary>Message storage, including atomic idempotency-record writes.</summary>
    IMessageStore Messages { get; }

    /// <summary>Role grants that scope an object's access to workspaces.</summary>
    IGrantStore Grants { get; }

    /// <summary>Read access to stored idempotency records.</summary>
    IIdempotencyStore Idempotency { get; }
}