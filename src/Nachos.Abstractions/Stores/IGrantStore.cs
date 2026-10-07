using Nachos.Abstractions.Domain;

namespace Nachos.Abstractions.Stores;

public interface IGrantStore
{
    Task AddAsync(GrantRecord grant, CancellationToken ct);

    Task RemoveAsync(GrantRecord grant, CancellationToken ct);

    /// <summary>Lists the grants of one object, or all grants when <paramref name="objectId"/> is null.</summary>
    Task<IReadOnlyList<GrantRecord>> ListAsync(string? objectId, CancellationToken ct);

    /// <summary>The distinct workspace names the object holds a grant on.</summary>
    Task<IReadOnlySet<string>> GetWorkspacesAsync(string objectId, CancellationToken ct);
}