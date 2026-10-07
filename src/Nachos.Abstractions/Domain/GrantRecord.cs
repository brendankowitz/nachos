namespace Nachos.Abstractions.Domain;

/// <summary>Grants <paramref name="Role"/> on a workspace (or, when null, on every workspace) to an object.</summary>
public sealed record GrantRecord(string ObjectId, string? WorkspaceName, string Role);