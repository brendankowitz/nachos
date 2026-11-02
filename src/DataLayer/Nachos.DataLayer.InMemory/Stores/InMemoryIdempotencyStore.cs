using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.DataLayer.InMemory.Storage;

namespace Nachos.DataLayer.InMemory.Stores;

internal sealed class InMemoryIdempotencyStore(InMemoryState state) : IIdempotencyStore
{
    public Task<IdempotencyRecord?> TryGetAsync(string workspaceName, string key, CancellationToken ct) =>
        state.Run(
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

    public Task<int> PurgeExpiredAsync(CancellationToken ct) =>
        state.Run(
            () =>
            {
                var now = state.Clock.GetUtcNow();
                var purged = 0;
                foreach (var workspace in state.AllWorkspaces())
                {
                    lock (workspace.Gate)
                    {
                        foreach (var key in workspace.Idempotency.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToList())
                        {
                            workspace.Idempotency.Remove(key);
                            purged++;
                        }
                    }
                }

                return purged;
            },
            ct);
}
