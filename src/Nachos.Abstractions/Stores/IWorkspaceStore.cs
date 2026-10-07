using System.Text.Json.Nodes;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;

namespace Nachos.Abstractions.Stores;

public interface IWorkspaceStore
{
    /// <summary>
    /// Returns the workspace, creating it from the given values if missing. An existing workspace is returned
    /// unchanged. Safe under concurrent calls for one name: exactly one row results.
    /// </summary>
    Task<WorkspaceRecord> GetOrCreateAsync(
        string name, JsonObject? metadata, JsonObject? configuration, CancellationToken ct);

    Task<WorkspaceRecord?> GetAsync(string name, CancellationToken ct);

    /// <summary>Replaces each non-null argument; a null argument leaves that field unchanged.</summary>
    /// <exception cref="NotFoundException">The workspace does not exist.</exception>
    Task<WorkspaceRecord> UpdateAsync(
        string name, JsonObject? metadata, JsonObject? configuration, CancellationToken ct);

    Task<Page<WorkspaceRecord>> ListAsync(FilterNode? filter, PageRequest page, CancellationToken ct);
}