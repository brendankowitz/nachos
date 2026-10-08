namespace Nachos.DataLayer.SqlServer.Entities;

/// <summary>A row of <c>dbo.PrincipalGrants</c>; a null <see cref="WorkspaceId"/> means every workspace.</summary>
internal sealed class PrincipalGrantEntity
{
    public long Id { get; set; }

    public string ObjectId { get; set; } = null!;

    public long? WorkspaceId { get; set; }

    public string Role { get; set; } = null!;
}
