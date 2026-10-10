namespace Nachos.DataLayer.SqlServer.Entities;

/// <summary>A row of <c>dbo.SessionPeers</c>: one membership, kept with <see cref="LeftAt"/> set after the peer leaves.</summary>
internal sealed class SessionPeerEntity
{
    public long WorkspaceId { get; set; }

    public long SessionId { get; set; }

    public long PeerId { get; set; }

    public string Configuration { get; set; } = null!;

    public DateTimeOffset JoinedAt { get; set; }

    public DateTimeOffset? LeftAt { get; set; }
}
