using Nachos.Abstractions;
using Nachos.Abstractions.Domain;

namespace Nachos.Core.Worker;

/// <summary>Processes the leased units of one <see cref="WorkTaskType"/>.</summary>
public interface IWorkHandler
{
    WorkTaskType TaskType { get; }

    /// <summary>When a unit of <see cref="TaskType"/> is due and how much of it one lease takes.</summary>
    WorkBatchPolicy BatchPolicy { get; }

    /// <summary>
    /// Processes <paramref name="lease"/> and completes it: a handler that returns normally has completed the unit itself,
    /// atomically with its writes. Any other outcome is an exception: the caller fails the lease, or, when the failure is transient or the host is
    /// shutting down, releases it without charging an attempt.
    /// </summary>
    /// <exception cref="LeaseLostException">
    /// The lease was lost and nothing was written; the caller stops without failing it, since the unit has a new owner.
    /// </exception>
    Task HandleAsync(WorkLease lease, CancellationToken cancellationToken);
}
