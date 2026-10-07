using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Abstractions.Stores;
using Nachos.DataLayer.InMemory.Storage;

namespace Nachos.DataLayer.InMemory.Stores;

internal sealed class InMemoryWorkspaceStore(InMemoryState state) : IWorkspaceStore
{
    public Task<WorkspaceRecord> GetOrCreateAsync(
        string name, JsonObject? metadata, JsonObject? configuration, CancellationToken ct) =>
        StoreTask.Run(
            () =>
            {
                var ownedMetadata = JsonCopy.Own(metadata);
                var ownedConfiguration = JsonCopy.Own(configuration);
                lock (state.Gate)
                {
                    if (!state.Workspaces.TryGetValue(name, out var entry))
                    {
                        var record = new WorkspaceRecord(
                            name, ownedMetadata, ownedConfiguration, LifecycleState.Active, state.Clock.GetUtcNow());
                        entry = new WorkspaceEntry(record, state.NextOrder());
                        state.Workspaces.Add(name, entry);
                    }

                    return JsonCopy.Out(entry.Record);
                }
            },
            ct);

    public Task<WorkspaceRecord?> GetAsync(string name, CancellationToken ct) =>
        StoreTask.Run(
            () =>
            {
                lock (state.Gate)
                {
                    return state.Workspaces.TryGetValue(name, out var entry) ? JsonCopy.Out(entry.Record) : null;
                }
            },
            ct);

    public Task<WorkspaceRecord> UpdateAsync(
        string name, JsonObject? metadata, JsonObject? configuration, CancellationToken ct) =>
        StoreTask.Run(
            () =>
            {
                var newMetadata = JsonCopy.OwnOptional(metadata);
                var newConfiguration = JsonCopy.OwnOptional(configuration);
                lock (state.Gate)
                {
                    var entry = state.Workspaces.GetValueOrDefault(name)
                        ?? throw new NotFoundException($"Workspace '{name}' not found.");
                    entry.Record = entry.Record with
                    {
                        Metadata = newMetadata ?? entry.Record.Metadata,
                        Configuration = newConfiguration ?? entry.Record.Configuration,
                    };
                    return JsonCopy.Out(entry.Record);
                }
            },
            ct);

    public Task<Page<WorkspaceRecord>> ListAsync(FilterNode? filter, PageRequest page, CancellationToken ct) =>
        StoreTask.Run(
            () =>
            {
                lock (state.Gate)
                {
                    var rows = state.Workspaces.Values
                        .Where(entry => InMemoryFilterEvaluator.Matches(filter, entry.Record))
                        .OrderBy(entry => entry.Record.CreatedAt)
                        .ThenBy(entry => entry.Order);
                    return Paging.ToPage(rows, page, entry => JsonCopy.Out(entry.Record));
                }
            },
            ct);
}
