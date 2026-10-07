using Nachos.Abstractions;
using Nachos.Abstractions.Domain;

namespace Nachos.DataLayer.InMemory.Storage;

/// <summary>All data of one <see cref="InMemoryMemoryStore"/>, shared by its sub-stores.</summary>
/// <remarks>
/// <para>
/// Locking: <see cref="Gate"/> guards <see cref="Workspaces"/>, every <see cref="WorkspaceEntry.Record"/> and
/// <see cref="Grants"/>. Each <see cref="WorkspaceEntry.Gate"/> guards that workspace's peers, sessions, memberships,
/// messages and idempotency records. The two are never held together: an operation looks its workspace up under
/// <see cref="Gate"/>, releases it, then takes the workspace's gate. That is safe because workspaces are never removed.
/// </para>
/// <para>
/// Critical sections are short and synchronous, and stored <c>JsonObject</c>s are only read or cloned inside them,
/// because even reading a <c>JsonObject</c> is not thread-safe (it materializes lazily).
/// </para>
/// </remarks>
internal sealed class InMemoryState(TimeProvider clock)
{
    private long _order;

    public TimeProvider Clock { get; } = clock;

    public Lock Gate { get; } = new();

    public Dictionary<string, WorkspaceEntry> Workspaces { get; } = new(StringComparer.Ordinal);

    public List<GrantRecord> Grants { get; } = [];

    /// <summary>A monotonically increasing insertion number, the tiebreak for rows with equal <c>CreatedAt</c>.</summary>
    public long NextOrder() => Interlocked.Increment(ref _order);

    /// <summary>The workspace, or null. Takes <see cref="Gate"/>.</summary>
    public WorkspaceEntry? FindWorkspace(string name)
    {
        lock (Gate)
        {
            return Workspaces.GetValueOrDefault(name);
        }
    }

    /// <summary>The workspace. Takes <see cref="Gate"/>.</summary>
    /// <exception cref="NotFoundException">The workspace does not exist.</exception>
    public WorkspaceEntry RequireWorkspace(string name) =>
        FindWorkspace(name) ?? throw new NotFoundException($"Workspace '{name}' not found.");
}
