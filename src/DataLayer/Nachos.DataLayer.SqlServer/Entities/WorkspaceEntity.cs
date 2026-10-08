namespace Nachos.DataLayer.SqlServer.Entities;

/// <summary>A row of <c>dbo.Workspaces</c>. JSON columns are mapped as their stored text.</summary>
internal sealed class WorkspaceEntity
{
    public long Id { get; set; }

    public string Name { get; set; } = null!;

    public byte LifecycleState { get; set; }

    public string Metadata { get; set; } = null!;

    public string Configuration { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }
}
