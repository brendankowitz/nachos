using System.Text.Json.Nodes;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;

namespace Nachos.Abstractions.Stores;

/// <summary>Workspace storage. See <see cref="IMemoryStore"/> for the rules shared by all stores.</summary>
public interface IWorkspaceStore
{
    /// <summary>
    /// Returns the workspace, creating it from the given values if missing (null values become <c>{}</c>). An
    /// existing workspace is returned unchanged. Concurrent calls for one name yield exactly one row and identical
    /// results, whichever caller's values won.
    /// </summary>
    Task<WorkspaceRecord> GetOrCreateAsync(
        string name, JsonObject? metadata, JsonObject? configuration, CancellationToken ct);

    /// <summary>Returns the workspace, or null if it does not exist.</summary>
    Task<WorkspaceRecord?> GetAsync(string name, CancellationToken ct);

    /// <summary>
    /// <b>Replaces</b> metadata and/or configuration wholesale when the argument is non-null (keys of the old value
    /// are gone); a null argument leaves that field unchanged.
    /// </summary>
    /// <exception cref="NotFoundException">The workspace does not exist.</exception>
    Task<WorkspaceRecord> UpdateAsync(
        string name, JsonObject? metadata, JsonObject? configuration, CancellationToken ct);

    /// <summary>Lists workspaces in creation order (reversed when <see cref="PageRequest.Reverse"/>).</summary>
    Task<Page<WorkspaceRecord>> ListAsync(FilterNode? filter, PageRequest page, CancellationToken ct);
}