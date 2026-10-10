namespace Nachos.DataLayer.SqlServer.Entities;

/// <summary>A row of <c>dbo.IdempotencyRecords</c>; <see cref="KeyHash"/> is the SHA-256 of <see cref="Key"/>.</summary>
internal sealed class IdempotencyRecordEntity
{
    public long Id { get; set; }

    public long WorkspaceId { get; set; }

    public byte[] KeyHash { get; set; } = null!;

    public string Key { get; set; } = null!;

    public string RequestHash { get; set; } = null!;

    public int ResponseStatus { get; set; }

    public string ResponseBody { get; set; } = null!;

    public DateTimeOffset ExpiresAt { get; set; }
}
