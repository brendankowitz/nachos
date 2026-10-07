using Nachos.Abstractions.Contracts;

namespace Nachos.DataLayer.InMemory.Storage;

/// <summary>A peer's membership of one session; a member who left keeps the row with <see cref="LeftAt"/> set.</summary>
internal sealed record Membership(SessionPeerConfig Config, DateTimeOffset JoinedAt, DateTimeOffset? LeftAt)
{
    public bool IsActive => LeftAt is null;
}
