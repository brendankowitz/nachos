namespace Nachos.Abstractions.Stores;

/// <summary>
/// The storage boundary. Providers take a <see cref="TimeProvider"/> and compute every time-based value
/// (<c>CreatedAt</c>, <c>JoinedAt</c>, <c>LeftAt</c>, idempotency expiry) from it, never from a database clock.
/// Ids and names are compared case-sensitively and ordinally.
/// </summary>
public interface IMemoryStore
{
    IWorkspaceStore Workspaces { get; }

    IPeerStore Peers { get; }

    ISessionStore Sessions { get; }

    IMessageStore Messages { get; }

    IGrantStore Grants { get; }

    IIdempotencyStore Idempotency { get; }
}