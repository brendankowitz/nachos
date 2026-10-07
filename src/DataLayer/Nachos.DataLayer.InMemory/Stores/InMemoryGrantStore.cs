using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.DataLayer.InMemory.Storage;

namespace Nachos.DataLayer.InMemory.Stores;

/// <summary>Grants live under the store-level gate, which also guards the workspace map they are checked against.</summary>
internal sealed class InMemoryGrantStore(InMemoryState state) : IGrantStore
{
    public Task AddAsync(GrantRecord grant, CancellationToken ct) =>
        StoreTask.Run(
            () =>
            {
                lock (state.Gate)
                {
                    if (grant.WorkspaceName is { } workspace && !state.Workspaces.ContainsKey(workspace))
                    {
                        throw new NotFoundException($"Workspace '{workspace}' not found.");
                    }

                    // Record equality compares the strings ordinally, so this is the idempotent-add check.
                    if (!state.Grants.Contains(grant))
                    {
                        state.Grants.Add(grant);
                    }
                }
            },
            ct);

    public Task RemoveAsync(GrantRecord grant, CancellationToken ct) =>
        StoreTask.Run(
            () =>
            {
                lock (state.Gate)
                {
                    state.Grants.Remove(grant);
                }
            },
            ct);

    public Task<IReadOnlyList<GrantRecord>> ListAsync(string? objectId, CancellationToken ct) =>
        StoreTask.Run<IReadOnlyList<GrantRecord>>(
            () =>
            {
                lock (state.Gate)
                {
                    return [.. state.Grants.Where(grant => objectId is null || string.Equals(grant.ObjectId, objectId, StringComparison.Ordinal))];
                }
            },
            ct);

    public Task<WorkspaceGrants> GetWorkspaceGrantsAsync(string objectId, CancellationToken ct) =>
        StoreTask.Run(
            () =>
            {
                var all = false;
                var workspaces = new HashSet<string>(StringComparer.Ordinal);
                lock (state.Gate)
                {
                    foreach (var grant in state.Grants)
                    {
                        if (!string.Equals(grant.ObjectId, objectId, StringComparison.Ordinal)
                            || !string.Equals(grant.Role, GrantRoles.Workspace, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        if (grant.WorkspaceName is null)
                        {
                            all = true;
                        }
                        else
                        {
                            workspaces.Add(grant.WorkspaceName);
                        }
                    }
                }

                return new WorkspaceGrants(all, workspaces);
            },
            ct);
}
