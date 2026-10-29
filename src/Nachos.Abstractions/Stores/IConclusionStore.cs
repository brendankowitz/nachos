using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;

namespace Nachos.Abstractions.Stores;

/// <summary>
/// Conclusion storage. See <see cref="IMemoryStore"/> for the rules shared by all stores. A collection is the set of
/// conclusions one observer peer holds about one observed peer; it is created by its first write.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tombstones.</b> <see cref="TombstoneAsync"/> hides a conclusion from every read and every dedup lookup at once;
/// <see cref="PurgeTombstonesAsync"/> removes it later.
/// </para>
/// <para>
/// <b>Vectors</b> are exactly <see cref="VectorSpace.Dimensions"/> long (any other length throws
/// <see cref="ArgumentException"/>) and compared by cosine distance, exactly (no approximate index). Conclusions
/// still waiting for their embedding are invisible to vector reads.
/// </para>
/// <para>
/// Every operation throws <see cref="NotFoundException"/> when the workspace does not exist, except
/// <see cref="PurgeTombstonesAsync"/>, which spans workspaces.
/// </para>
/// </remarks>
public interface IConclusionStore
{
    /// <summary>Returns the collection with its dream counters, or null if nothing was ever written to it.</summary>
    Task<CollectionRecord?> GetCollectionAsync(
        string workspaceName, string observerName, string observedName, CancellationToken ct);

    /// <summary>
    /// Dedup lookup: the oldest conclusion in the collection with this <paramref name="level"/> and exactly (ordinally)
    /// this <paramref name="content"/>, or null. Callers normalize content before inserting and before looking up.
    /// </summary>
    /// <param name="restrictToSession">When non-null, only conclusions of that session are considered.</param>
    Task<ConclusionRecord?> FindExactAsync(
        string workspaceName,
        string observerName,
        string observedName,
        ConclusionLevel level,
        string content,
        string? restrictToSession,
        CancellationToken ct);

    /// <summary>
    /// Dedup lookup: the conclusion in the collection nearest to <paramref name="vector"/>, if its distance is at most
    /// <paramref name="maxDistance"/>; ties go to the oldest. Null when there is none.
    /// </summary>
    /// <param name="restrictToSession">When non-null, only conclusions of that session are considered.</param>
    Task<ConclusionMatch?> FindNearestAsync(
        string workspaceName,
        string observerName,
        string observedName,
        ReadOnlyMemory<float> vector,
        double maxDistance,
        string? restrictToSession,
        CancellationToken ct);

    /// <summary>
    /// Lists the workspace's conclusions matching <paramref name="filter"/> (fields of
    /// <see cref="ResourceKind.Conclusion"/>) in <paramref name="order"/>.
    /// </summary>
    Task<Page<ConclusionRecord>> ListAsync(
        string workspaceName, FilterNode? filter, ConclusionOrder order, PageRequest page, CancellationToken ct);

    /// <summary>
    /// The <paramref name="topK"/> conclusions matching <paramref name="filter"/> nearest to <paramref name="vector"/>,
    /// nearest first (ties oldest first), leaving out any farther than <paramref name="maxDistance"/> when it is set.
    /// </summary>
    Task<IReadOnlyList<ConclusionMatch>> QueryAsync(
        string workspaceName,
        ReadOnlyMemory<float> vector,
        int topK,
        double? maxDistance,
        FilterNode? filter,
        CancellationToken ct);

    /// <summary>Looks the conclusion up by its case-sensitive public id; null when it is missing or tombstoned.</summary>
    Task<ConclusionRecord?> GetAsync(string workspaceName, string publicId, CancellationToken ct);

    /// <summary>
    /// Applies one derivation and completes its lease, in one transaction: checks that <paramref name="lease"/> is
    /// still held, inserts <see cref="DerivationChanges.Inserts"/>, adds the corroborations, applies the replacements,
    /// updates the dream counters of every collection it inserted into (<c>NewExplicitCount</c> += explicit inserts,
    /// <c>LastActivityAt</c> = now), marks the lease's items succeeded and releases the unit.
    /// </summary>
    /// <remarks>
    /// A corroboration or replacement naming a conclusion that is missing or tombstoned is skipped, so a concurrent
    /// delete wins. Any exception leaves everything unchanged, including the lease and its items.
    /// </remarks>
    /// <exception cref="LeaseLostException">The lease is no longer held.</exception>
    /// <exception cref="NotFoundException">An insert names a peer or session that does not exist.</exception>
    Task CommitDerivationAsync(WorkLease lease, DerivationChanges changes, CancellationToken ct);

    /// <summary>
    /// Inserts conclusions created through the API, all or nothing and without deduplication. Each starts with
    /// <c>TimesDerived = 1</c> and a new public id; one without an embedding is pending.
    /// </summary>
    /// <exception cref="NotFoundException">A conclusion names a peer or session that does not exist.</exception>
    Task<IReadOnlyList<ConclusionRecord>> InsertAsync(
        string workspaceName, IReadOnlyList<NewConclusion> conclusions, CancellationToken ct);

    /// <summary>Tombstones the conclusion. A missing or already tombstoned conclusion is a no-op.</summary>
    Task TombstoneAsync(string workspaceName, string publicId, CancellationToken ct);

    /// <summary>Deletes conclusions tombstoned at least <paramref name="retention"/> ago, in every workspace.</summary>
    /// <returns>How many conclusions were deleted.</returns>
    Task<int> PurgeTombstonesAsync(TimeSpan retention, CancellationToken ct);
}
