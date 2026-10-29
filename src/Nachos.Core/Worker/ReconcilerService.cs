using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Abstractions.Work;

namespace Nachos.Core.Worker;

/// <summary>
/// The cluster's housekeeping, run by one replica at a time under the <see cref="WorkUnitKeys.Reconciler"/> lease. Each
/// tick embeds pending conclusions, deletes tombstoned conclusions older than
/// <see cref="ReconcilerOptions.TombstoneRetention"/>, prunes succeeded work items older than
/// <see cref="ReconcilerOptions.CompletedRetention"/>, and deletes expired idempotency records.
/// </summary>
/// <remarks>
/// <para>
/// A replica that holds the lease keeps it (renewed every third of <see cref="ReconcilerOptions.LeaseDuration"/>) until
/// it stops, then releases it; the others try to take it every tick. Every step is safe to repeat, so a lease lost
/// mid-tick at worst runs a step twice.
/// </para>
/// <para>
/// Embedding: pending conclusions are embedded oldest first in batches of
/// <see cref="ReconcilerOptions.EmbeddingBatchSize"/>, while batches come back full. When a batch call fails each of its
/// conclusions is retried alone, so one bad conclusion cannot hold back the rest. A conclusion that has failed
/// <see cref="ReconcilerOptions.EmbeddingMaxAttempts"/> times, once per tick, is marked failed and leaves the backlog
/// (until its content is replaced). Failure counts live in memory and restart with the lease holder. Without an
/// embedding generator nothing is embedded and the other steps still run.
/// </para>
/// <para>A tick with any failure delays the next one exponentially, up to <see cref="ReconcilerOptions.MaxInterval"/>.</para>
/// </remarks>
public sealed partial class ReconcilerService : BackgroundService
{
    private readonly IMemoryStore _store;
    private readonly TimeProvider _clock;
    private readonly ReconcilerOptions _options;
    private readonly ILogger<ReconcilerService> _logger;
    private readonly IEmbeddingGenerator<string, Embedding<float>>? _embeddings;
    private readonly Dictionary<(string Workspace, string Conclusion), int> _embeddingFailures = [];
    private readonly string _owner = $"{Environment.MachineName}:{Environment.ProcessId}:reconciler:{Guid.NewGuid():N}";

    public ReconcilerService(
        IMemoryStore store,
        TimeProvider clock,
        IOptions<ReconcilerOptions> options,
        ILogger<ReconcilerService> logger,
        IEmbeddingGenerator<string, Embedding<float>>? embeddings = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
        _embeddings = embeddings;
    }

    private string Owner => _owner.Length <= WorkLease.MaxOwnerLength ? _owner : _owner[^WorkLease.MaxOwnerLength..];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_embeddings is null)
        {
            LogNoGenerator();
        }

        // Task.Run: a synchronous store (in-memory) would otherwise run the first tick inline, inside host startup.
        await Task.Run(() => RunAsync(stoppingToken), CancellationToken.None).ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        WorkLease? lease = null;
        LeaseKeeper? keeper = null;
        var failedTicks = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (keeper is { IsLost: true })
                {
                    await keeper.DisposeAsync().ConfigureAwait(false);
                    (keeper, lease) = (null, null);
                    _embeddingFailures.Clear();
                }

                if (keeper is null)
                {
                    lease = await TryLeaseAsync(ct).ConfigureAwait(false);
                    if (lease is not null)
                    {
                        LogLeader(Owner);
                        keeper = LeaseKeeper.Start(_store.WorkQueue, lease, _options.LeaseDuration, _clock, _logger, ct);
                    }
                }

                if (keeper is not null)
                {
                    failedTicks = await TryTickAsync(keeper, ct).ConfigureAwait(false) ? 0 : failedTicks + 1;
                }

                var delay = failedTicks == 0
                    ? _options.Interval
                    : Backoff.Delay(_options.Interval * 2, _options.MaxInterval, failedTicks);
                await Task.Delay(delay, _clock, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            if (keeper is not null)
            {
                await keeper.DisposeAsync().ConfigureAwait(false);
                if (!keeper.IsLost)
                {
                    await ReleaseAsync(lease!).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task<WorkLease?> TryLeaseAsync(CancellationToken ct)
    {
        try
        {
            return await _store.WorkQueue.TryLeaseAsync(WorkUnitKeys.Reconciler, Owner, _options.LeaseDuration, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogLeaseFailed(ex);
            return null;
        }
    }

    /// <summary>Releases the lease so another replica can take over at once.</summary>
    private async Task ReleaseAsync(WorkLease lease)
    {
        try
        {
            // CancellationToken.None: the stopping token is already cancelled, and the host's shutdown timeout bounds this.
            await _store.WorkQueue.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The lease expires on its own; another replica then takes over.
            LogReleaseFailed(ex);
        }
    }

    /// <returns>True when every step of the tick succeeded.</returns>
    private async Task<bool> TryTickAsync(LeaseKeeper keeper, CancellationToken ct)
    {
        try
        {
            return await TickAsync(keeper.Token).ConfigureAwait(false);
        }
        catch (Exception) when (keeper.IsLost && !ct.IsCancellationRequested)
        {
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogTickFailed(ex);
            return false;
        }
    }

    private async Task<bool> TickAsync(CancellationToken ct)
    {
        var embedding = _embeddings is null ? new EmbeddingOutcome(0, 0, 0) : await EmbedPendingAsync(_embeddings, ct).ConfigureAwait(false);
        var tombstones = await _store.Conclusions.PurgeTombstonesAsync(_options.TombstoneRetention, ct).ConfigureAwait(false);
        var items = await _store.WorkQueue.PruneSucceededAsync(_options.CompletedRetention, ct).ConfigureAwait(false);
        var idempotency = await _store.Idempotency.PurgeExpiredAsync(ct).ConfigureAwait(false);
        LogTick(embedding.Embedded, embedding.Failed, embedding.MarkedFailed, tombstones, items, idempotency);
        return embedding.Failed == 0;
    }

    private async Task<EmbeddingOutcome> EmbedPendingAsync(IEmbeddingGenerator<string, Embedding<float>> generator, CancellationToken ct)
    {
        var total = new EmbeddingOutcome(0, 0, 0);
        while (true)
        {
            var pending = await _store.Embeddings.ListPendingAsync(_options.EmbeddingBatchSize, ct).ConfigureAwait(false);
            if (pending.Count == 0)
            {
                return total;
            }

            var batch = await EmbedBatchAsync(generator, pending, ct).ConfigureAwait(false);
            total = new EmbeddingOutcome(
                total.Embedded + batch.Embedded, total.Failed + batch.Failed, total.MarkedFailed + batch.MarkedFailed);
            // A failed conclusion stays pending until it is marked failed, so keep going only after a clean, full batch.
            if (batch.Failed > 0 || pending.Count < _options.EmbeddingBatchSize)
            {
                return total;
            }
        }
    }

    private async Task<EmbeddingOutcome> EmbedBatchAsync(
        IEmbeddingGenerator<string, Embedding<float>> generator, IReadOnlyList<PendingEmbedding> pending, CancellationToken ct)
    {
        IReadOnlyList<ReadOnlyMemory<float>> vectors;
        try
        {
            vectors = await GenerateAsync(generator, pending, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (pending.Count == 1)
            {
                LogEmbeddingFailed(ex, pending[0].ConclusionId);
                return await RecordFailureAsync(pending[0], ct).ConfigureAwait(false)
                    ? new EmbeddingOutcome(0, 1, 1)
                    : new EmbeddingOutcome(0, 1, 0);
            }

            LogBatchFailed(ex, pending.Count);
            return await EmbedOneByOneAsync(generator, pending, ct).ConfigureAwait(false);
        }

        for (var i = 0; i < pending.Count; i++)
        {
            await SaveAsync(pending[i], vectors[i], ct).ConfigureAwait(false);
        }

        return new EmbeddingOutcome(pending.Count, 0, 0);
    }

    private async Task<EmbeddingOutcome> EmbedOneByOneAsync(
        IEmbeddingGenerator<string, Embedding<float>> generator, IReadOnlyList<PendingEmbedding> pending, CancellationToken ct)
    {
        int embedded = 0, failed = 0, markedFailed = 0;
        foreach (var item in pending)
        {
            ReadOnlyMemory<float> vector;
            try
            {
                vector = (await GenerateAsync(generator, [item], ct).ConfigureAwait(false))[0];
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                LogEmbeddingFailed(ex, item.ConclusionId);
                failed++;
                markedFailed += await RecordFailureAsync(item, ct).ConfigureAwait(false) ? 1 : 0;
                continue;
            }

            await SaveAsync(item, vector, ct).ConfigureAwait(false);
            embedded++;
        }

        return new EmbeddingOutcome(embedded, failed, markedFailed);
    }

    /// <exception cref="InvalidOperationException">The generator returned the wrong number or size of vectors.</exception>
    private static async Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateAsync(
        IEmbeddingGenerator<string, Embedding<float>> generator, IReadOnlyList<PendingEmbedding> pending, CancellationToken ct)
    {
        var embeddings = await generator.GenerateAsync(pending.Select(p => p.Content), cancellationToken: ct).ConfigureAwait(false);
        if (embeddings.Count != pending.Count || embeddings.Any(e => e.Vector.Length != VectorSpace.Dimensions))
        {
            throw new InvalidOperationException(
                $"The embedding generator returned {embeddings.Count} vectors for {pending.Count} conclusions, or vectors " +
                $"that are not {VectorSpace.Dimensions} long. Check the Embeddings configuration.");
        }

        return [.. embeddings.Select(e => e.Vector)];
    }

    private async Task SaveAsync(PendingEmbedding pending, ReadOnlyMemory<float> vector, CancellationToken ct)
    {
        await _store.Embeddings.SaveAsync(pending, vector, ct).ConfigureAwait(false);
        _embeddingFailures.Remove((pending.WorkspaceName, pending.ConclusionId));
    }

    /// <returns>True when this failure was the last allowed and the conclusion was marked failed.</returns>
    private async Task<bool> RecordFailureAsync(PendingEmbedding pending, CancellationToken ct)
    {
        var key = (pending.WorkspaceName, pending.ConclusionId);
        var failures = _embeddingFailures.GetValueOrDefault(key) + 1;
        if (failures < _options.EmbeddingMaxAttempts)
        {
            _embeddingFailures[key] = failures;
            return false;
        }

        await _store.Embeddings.MarkFailedAsync(pending, ct).ConfigureAwait(false);
        _embeddingFailures.Remove(key);
        LogMarkedFailed(pending.ConclusionId, pending.WorkspaceName, failures);
        return true;
    }

    private sealed record EmbeddingOutcome(int Embedded, int Failed, int MarkedFailed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No embedding generator is registered; the reconciler will not embed pending conclusions.")]
    private partial void LogNoGenerator();

    [LoggerMessage(Level = LogLevel.Information, Message = "{Owner} now holds the reconciler lease.")]
    private partial void LogLeader(string owner);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Taking the reconciler lease failed; retrying next interval.")]
    private partial void LogLeaseFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Releasing the reconciler lease failed; another replica takes over when it expires.")]
    private partial void LogReleaseFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "A reconciler tick failed; the next one is delayed.")]
    private partial void LogTickFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Embedding a batch of {Count} conclusions failed; embedding them one by one.")]
    private partial void LogBatchFailed(Exception exception, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Embedding conclusion {ConclusionId} failed.")]
    private partial void LogEmbeddingFailed(Exception exception, string conclusionId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Conclusion {ConclusionId} in workspace {WorkspaceName} failed to embed {Failures} times; marked failed, it is invisible to semantic reads until its content changes.")]
    private partial void LogMarkedFailed(string conclusionId, string workspaceName, int failures);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Reconciler tick: embedded {Embedded}, {Failed} embedding failures ({MarkedFailed} marked failed), purged {Tombstones} tombstones, pruned {Items} work items and {Idempotency} idempotency records.")]
    private partial void LogTick(int embedded, int failed, int markedFailed, int tombstones, int items, int idempotency);
}
