using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Abstractions.Work;
using Nachos.Core.Configuration;
using Nachos.Core.Worker;

namespace Nachos.Core.Deletion;

/// <summary>
/// The <see cref="WorkTaskType.Deletion"/> handler (spec §13.1, "The deletion job"): cancels the target's other work, purges
/// its rows in bounded batches, then removes it, after which its name is free.
/// </summary>
/// <remarks>
/// <para>
/// Every step is resumable: the purge state is what is left in the store, so a job that fails or loses its lease is
/// retried on the same item (with the queue's backoff) and carries on where it stopped. The lease is renewed between
/// batches; a lost lease ends the job at once with nothing more written. A dead-lettered job leaves the target
/// <see cref="LifecycleState.Deleting"/>, so writes stay blocked, and is listed with its error in <c>W/jobs</c>.
/// </para>
/// <para>
/// D7: tombstoned conclusions get no <c>card_refresh</c> dream in M3; the dream handler arrives in M5.
/// </para>
/// </remarks>
public sealed partial class DeletionService : IWorkHandler
{
    private readonly IMemoryStore _store;
    private readonly TimeSpan _leaseDuration;
    private readonly int _batchSize;
    private readonly ILogger<DeletionService> _logger;

    public DeletionService(
        IMemoryStore store, IOptions<NachosOptions> nachosOptions, IOptions<DeletionOptions> options, ILogger<DeletionService> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(nachosOptions);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _leaseDuration = nachosOptions.Value.Deriver.LeaseDuration;
        _batchSize = options.Value.BatchSize;
        _logger = logger;
    }

    public WorkTaskType TaskType => WorkTaskType.Deletion;

    public WorkBatchPolicy BatchPolicy => WorkBatchPolicy.Immediate;

    /// <exception cref="ArgumentException">The lease has no items.</exception>
    /// <exception cref="FormatException">The lease's item is not a well-formed deletion item.</exception>
    /// <exception cref="LeaseLostException">The lease was lost; nothing more was written.</exception>
    public async Task HandleAsync(WorkLease lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Items.Count == 0)
        {
            throw new ArgumentException($"Lease on '{lease.WorkUnitKey}' has no deletion item.", nameof(lease));
        }

        var target = DeletionPayload.Read(lease.Items[0]);
        await _store.Deletion.CancelOtherWorkAsync(lease, target, cancellationToken).ConfigureAwait(false);
        var batches = 1;
        while (await _store.Deletion.PurgeBatchAsync(lease, target, _batchSize, cancellationToken).ConfigureAwait(false))
        {
            await _store.WorkQueue.RenewAsync(lease, _leaseDuration, cancellationToken).ConfigureAwait(false);
            batches++;
        }

        await _store.Deletion.RemoveAsync(lease, target, cancellationToken).ConfigureAwait(false);
        LogRemoved(lease.Items[0].Id, target.Kind, target.WorkspaceName, target.Name, batches);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Deletion job {JobId} removed {Kind} '{Name}' of workspace '{Workspace}' after {Batches} purge batches.")]
    private partial void LogRemoved(long jobId, DeletionTargetKind kind, string workspace, string name, int batches);
}
