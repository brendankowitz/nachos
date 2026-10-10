namespace Nachos.DataLayer.SqlServer.Entities;

/// <summary>A row of <c>dbo.Sessions</c>.</summary>
internal sealed class SessionEntity
{
    public long Id { get; set; }

    public long WorkspaceId { get; set; }

    public string Name { get; set; } = null!;

    public byte LifecycleState { get; set; }

    public long NextMessageSeq { get; set; }

    public string Metadata { get; set; } = null!;

    public string Configuration { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }
}
