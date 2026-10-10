using Nachos.Abstractions.Domain;

namespace Nachos.Abstractions.Stores;

/// <summary>Role grants that scope an object's access to workspaces.</summary>
public interface IGrantStore
{
    /// <summary>
    /// Adds the grant. Adding a grant that already exists is an idempotent no-op. A grant with a null
    /// <see cref="GrantRecord.WorkspaceName"/> means "all workspaces" for the role.
    /// </summary>
    /// <exception cref="NotFoundException">The grant names a workspace that does not exist.</exception>
    Task AddAsync(GrantRecord grant, CancellationToken ct);

    /// <summary>Removes the grant. Removing a grant that does not exist is a no-op.</summary>
    Task RemoveAsync(GrantRecord grant, CancellationToken ct);

    /// <summary>Lists the grants of one object, or all grants when <paramref name="objectId"/> is null.</summary>
    Task<IReadOnlyList<GrantRecord>> ListAsync(string? objectId, CancellationToken ct);

    /// <summary>
    /// Resolves the workspaces the object may access from its <see cref="GrantRoles.Workspace"/> grants only: a grant
    /// without a workspace name sets <see cref="WorkspaceGrants.AllWorkspaces"/>, the others are collected in
    /// <see cref="WorkspaceGrants.Workspaces"/>. Grants of other roles (for example <see cref="GrantRoles.Admin"/>)
    /// contribute nothing.
    /// </summary>
    Task<WorkspaceGrants> GetWorkspaceGrantsAsync(string objectId, CancellationToken ct);
}