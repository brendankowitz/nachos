using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Abstractions.Work;
using Nachos.DataLayer.InMemory.Storage;

namespace Nachos.DataLayer.InMemory.Stores;

/// <remarks>
/// <see cref="SaveAsync"/> holds the workspace gate and then the queue gate, which makes the lease check, the write and
/// the completion one atomic step, as <see cref="InMemoryConclusionStore.CommitDerivationAsync"/> does.
/// </remarks>
internal sealed class InMemorySummaryStore(InMemoryState state) : ISummaryStore
{
    public Task<SummaryRecord?> GetAsync(string workspaceName, string sessionName, SummaryKind kind, CancellationToken ct) =>
        state.Run(
            () =>
            {
                var workspace = state.RequireWorkspace(workspaceName);
                lock (workspace.Gate)
                {
                    return workspace.RequireSession(sessionName).Summaries.GetValueOrDefault(kind);
                }
            },
            ct);

    public Task<IReadOnlyList<SummaryRecord>> ListAsync(string workspaceName, string sessionName, CancellationToken ct) =>
        state.Run<IReadOnlyList<SummaryRecord>>(
            () =>
            {
                var workspace = state.RequireWorkspace(workspaceName);
                lock (workspace.Gate)
                {
                    return [.. workspace.RequireSession(sessionName).Summaries.Values.OrderBy(summary => summary.Kind)];
                }
            },
            ct);

    public Task SaveAsync(
        WorkLease lease, string workspaceName, string sessionName, IReadOnlyList<NewSummary> summaries, CancellationToken ct) =>
        state.Run(
            () =>
            {
                ArgumentNullException.ThrowIfNull(lease);
                ArgumentNullException.ThrowIfNull(summaries);
                foreach (var summary in summaries)
                {
                    ArgumentNullException.ThrowIfNull(summary);
                    ArgumentNullException.ThrowIfNull(summary.Content);
                    ArgumentNullException.ThrowIfNull(summary.CoversThroughMessageId);
                }

                if (summaries.DistinctBy(summary => summary.Kind).Count() != summaries.Count)
                {
                    throw new ArgumentException("At most one summary of each kind can be saved under one lease.", nameof(summaries));
                }

                if (!string.Equals(lease.WorkUnitKey, WorkUnitKeys.Summary(workspaceName, sessionName), StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"The lease on '{lease.WorkUnitKey}' is not on the summary unit of session '{sessionName}'.", nameof(lease));
                }

                if (state.FindWorkspace(workspaceName) is not { } workspace)
                {
                    lock (state.Queue.Gate)
                    {
                        state.Queue.RequireHeld(lease);
                        state.Queue.Complete(lease, state.Clock.GetUtcNow(), InMemoryConclusionStore.ResourceDeleted);
                        return;
                    }
                }

                lock (workspace.Gate)
                {
                    lock (state.Queue.Gate)
                    {
                        state.Queue.RequireHeld(lease);
                        var now = state.Clock.GetUtcNow();
                        if (workspace.IsDeleting || workspace.Sessions.GetValueOrDefault(sessionName) is not { IsDeleting: false } session)
                        {
                            state.Queue.Complete(lease, now, InMemoryConclusionStore.ResourceDeleted);
                            return;
                        }

                        var through = summaries.FirstOrDefault(summary =>
                            session.FindMessage(summary.CoversThroughMessageId)?.Seq != summary.CoversThroughSeq);
                        if (through is not null)
                        {
                            throw new NotFoundException(
                                $"Message '{through.CoversThroughMessageId}' with seq {through.CoversThroughSeq} not found in session '{sessionName}'.");
                        }

                        foreach (var summary in summaries.Where(summary =>
                            session.Summaries.GetValueOrDefault(summary.Kind) is not { } stored || summary.CoversThroughSeq > stored.CoversThroughSeq))
                        {
                            session.Summaries[summary.Kind] = new SummaryRecord(
                                summary.Kind, summary.Content, summary.CoversThroughMessageId, summary.CoversThroughSeq, summary.TokenCount, now);
                        }

                        state.Queue.Complete(lease, now);
                    }
                }
            },
            ct);
}
