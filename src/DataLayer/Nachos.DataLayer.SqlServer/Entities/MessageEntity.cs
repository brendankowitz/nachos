namespace Nachos.DataLayer.SqlServer.Entities;

/// <summary>A row of <c>dbo.Messages</c>.</summary>
internal sealed class MessageEntity
{
    public long Id { get; set; }

    public long WorkspaceId { get; set; }

    public long SessionId { get; set; }

    public long PeerId { get; set; }

    public string PublicId { get; set; } = null!;

    public long Seq { get; set; }

    public string Content { get; set; } = null!;

    public int TokenCount { get; set; }

    public string Metadata { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }
}
