using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.DataLayer.InMemory.Storage;

namespace Nachos.DataLayer.InMemory.Stores;

internal sealed class InMemoryIdempotencyStore(InMemoryState state) : IIdempotencyStore
{
    public Task<IdempotencyRecord?> TryGetAsync(string workspaceName, string key, CancellationToken ct) =>
        StoreTask.Run(
            () =>
            {
                if (state.FindWorkspace(workspaceName) is not { } workspace)
                {
                    return null;
                }

                lock (workspace.Gate)
                {
                    return workspace.Idempotency.TryGetValue(key, out var record)
                        && record.ExpiresAt > state.Clock.GetUtcNow()
                            ? record
                            : null;
                }
            },
            ct);
}
