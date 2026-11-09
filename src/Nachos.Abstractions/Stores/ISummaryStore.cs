using Nachos.Abstractions.Domain;

namespace Nachos.Abstractions.Stores;

/// <summary>
/// Session summaries: at most one current summary per (session, <see cref="SummaryKind"/>). See
/// <see cref="IMemoryStore"/> for the rules shared by all stores.
/// </summary>
public interface ISummaryStore
{
    /// <summary>The session's current summary of <paramref name="kind"/>, or null when none has been saved.</summary>
    /// <exception cref="NotFoundException">The session (or its workspace) does not exist.</exception>
    Task<SummaryRecord?> GetAsync(string workspaceName, string sessionName, SummaryKind kind, CancellationToken ct);

    /// <summary>The session's current summaries, short before long; empty when none has been saved.</summary>
    /// <exception cref="NotFoundException">The session (or its workspace) does not exist.</exception>
    Task<IReadOnlyList<SummaryRecord>> ListAsync(string workspaceName, string sessionName, CancellationToken ct);

    /// <summary>
    /// Saves one lease's summaries and completes the lease, in one transaction, fenced like
    /// <see cref="IConclusionStore.CommitDerivationAsync"/>: checks that <paramref name="lease"/> is still held, replaces
    /// the session's summary of each <see cref="NewSummary.Kind"/> only when the new one covers a greater
    /// <see cref="NewSummary.CoversThroughSeq"/> than the stored one (an older or equal one is ignored), stamping its
    /// <see cref="SummaryRecord.CreatedAt"/> from the store's clock, marks the lease's items succeeded and releases the unit.
    /// </summary>
    /// <remarks>
    /// Summary items carry no tokens, so one lease can hold a short and a long item; their summaries are saved together.
    /// When the workspace or the session is being deleted, or no longer exists, the summaries are discarded: nothing is
    /// written, and the lease's items complete as succeeded with the error <c>resource_deleted</c>. Any exception leaves
    /// everything unchanged, including the lease and its items.
    /// </remarks>
    /// <param name="lease">A lease on the session's unit, <see cref="Work.WorkUnitKeys.Summary"/>.</param>
    /// <param name="summaries">At most one per kind; none just completes the lease.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="summaries"/> has two of one kind, or <paramref name="lease"/> is not on the session's summary unit.
    /// </exception>
    /// <exception cref="LeaseLostException">The lease is no longer held.</exception>
    /// <exception cref="NotFoundException">
    /// A summary's <see cref="NewSummary.CoversThroughMessageId"/> names no message of the session with that
    /// <see cref="NewSummary.CoversThroughSeq"/>.
    /// </exception>
    Task SaveAsync(
        WorkLease lease, string workspaceName, string sessionName, IReadOnlyList<NewSummary> summaries, CancellationToken ct);
}
