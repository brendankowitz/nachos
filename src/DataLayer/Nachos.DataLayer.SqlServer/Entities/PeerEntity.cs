namespace Nachos.DataLayer.SqlServer.Entities;

/// <summary>A row of <c>dbo.Peers</c>.</summary>
internal sealed class PeerEntity
{
    public long Id { get; set; }

    public long WorkspaceId { get; set; }

    public string Name { get; set; } = null!;

    public bool IsInternal { get; set; }

    public string Metadata { get; set; } = null!;

    public string Configuration { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }
}
