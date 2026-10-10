namespace Nachos.Abstractions.Domain;

/// <summary>
/// Grants <paramref name="Role"/> to an object. For <see cref="GrantRoles.Workspace"/> a null
/// <paramref name="WorkspaceName"/> means "all workspaces"; a non-null name must refer to an existing workspace.
/// </summary>
public sealed record GrantRecord(string ObjectId, string? WorkspaceName, string Role);