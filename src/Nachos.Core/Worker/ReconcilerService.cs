using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Abstractions.Work;
using Nachos.Core.Llm;
using Nachos.Core.Memory;

namespace Nachos.Core.Worker;

/// <summary>
/// The cluster's housekeeping, run by one replica at a time under the <see cref="WorkUnitKeys.Reconciler"/> lease. Each
/// tick embeds pending conclusions and message chunks, chunks one batch (<see cref="ReconcilerOptions.BackfillBatch"/>) of
/// messages that have none (when a <see cref="MessageChunker"/> is registered), deletes tombstoned conclusions older than
/// <see cref="ReconcilerOptions.TombstoneRetention"/>, prunes succeeded work items older than
/// <see cref="ReconcilerOptions.CompletedRetention"/>, refreshes the backlog gauges of <see cref="WorkerMetrics"/>, and
/// deletes expired idempotency records.
/// </summary>
/// <remarks>
/// <para>
/// A replica that holds the lease keeps it (renewed every third of <see cref="ReconcilerOptions.LeaseDuration"/>) until
/// it stops, then releases it; the others try to take it every tick. Every step is safe to repeat, so a lease lost
/// mid-tick at worst runs a step twice.
/// </para>
/// <para>
/// Embedding: pending conclusions are embedded oldest first in batches of
/// <see cref="ReconcilerOptions.EmbeddingBatchSize"/>, while batches come back full (at most 100 batches per tick). When a
/// batch call fails each of its conclusions is retried alone, so one bad conclusion cannot hold back the rest; but a
/// failure that would fail any input ends the step for the tick instead: the service failing (429, 503, a gateway error,
/// a timeout, a connection error; not 500, which one input can provoke) or a misconfigured generator (401, 403, 404,
/// vectors of the wrong number or size). Such failures never count. Any other failure counts only when another
/// embedding of the tick succeeded, or when the service rejected that input (HTTP 400) and not every failure of the
/// tick was a rejection, so a setting that makes every request a 400 cannot fail the whole backlog. During an outage
/// nothing counts. A conclusion with
/// <see cref="ReconcilerOptions.EmbeddingMaxAttempts"/> counted failures, at most one per tick, is marked failed and
/// leaves the backlog (until its content is replaced). Failure counts live in memory and restart with the lease holder.
/// From the <see cref="ReconcilerOptions.FailedEmbeddingTicksBeforeError"/>th consecutive tick in which embeddings
/// failed and none succeeded, each such tick is logged as an error. Without an embedding generator nothing is embedded
/// and the other steps still run.
/// </para>
/// <para>
/// A tick whose step throws (the store is unavailable) delays the next one exponentially, up to
/// <see cref="ReconcilerOptions.MaxInterval"/>; embedding failures of single conclusions do not.
/// </para>
/// </remarks>
public sealed partial class ReconcilerService : BackgroundService
{
    /// <summary>Bounds one tick's embedding work, so the other steps run even under a large backlog.</summary>
    private const int MaxBatchesPerTick = 100;

    private readonly IMemoryStore _store;
    private readonly TimeProvider _clock;
    private readonly ReconcilerOptions _options;
    private readonly IServiceScopeFactory _scopes;
    private readonly WorkerMetrics _metrics;
    private readonly ILogger<ReconcilerService> _logger;
    private readonly IEmbeddingGenerator<string, Embedding<float>>? _embeddings;
    private readonly MessageChunker? _chunker;
    private readonly string _embeddingModel;
    private readonly Dictionary<(PendingEmbeddingKind Kind, string Workspace, string Source, int Chunk), int> _embeddingFailures = [];
    private int _failedEmbeddingTicks;
    private readonly string _owner = $"{Environment.MachineName}:{Environment.ProcessId}:reconciler:{Guid.NewGuid():N}";

    /// <param name="scopes">Reads the handlers' batch policies, which define a due unit for the backlog gauges.</param>
    public ReconcilerService(
        IMemoryStore store,
        TimeProvider clock,
        IOptions<ReconcilerOptions> options,
        IServiceScopeFactory scopes,
        WorkerMetrics metrics,
        ILogger<ReconcilerService> logger,
        IEmbeddingGenerator<string, Embedding<float>>? embeddings = null,
        MessageChunker? chunker = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _clock = clock;
        _options = options.Value;
        _scopes = scopes;
        _metrics = metrics;
        _logger = logger;
        _embeddings = embeddings;
        _chunker = chunker;
        _embeddingModel = embeddings is null ? EmbeddingModelName.Unknown : EmbeddingModelName.Of(embeddings);
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
        var policies = await WorkHandlerPolicies.ReadAsync(_scopes).ConfigureAwait(false);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (keeper is { IsLost: true })
                {
                    await keeper.DisposeAsync().ConfigureAwait(false);
                    (keeper, lease) = (null, null);
                    _embeddingFailures.Clear();
                    _failedEmbeddingTicks = 0;
                    _metrics.ClearBacklog();
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
                    failedTicks = await TryTickAsync(keeper, policies, ct).ConfigureAwait(false) ? 0 : failedTicks + 1;
                }

                var delay = failedTicks == 0
                    ? _options.Interval
                    : Backoff.Delay(_options.Interval * 2, _options.MaxInterval, failedTicks);
                await Task.Delay(delay, _clock, ct).ConfigureAwait(false);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Shutdown; a provider may report its own cancellation as something other than OperationCanceledException.
        }
        finally
        {
            _metrics.ClearBacklog();
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

    /// <returns>False when a step threw; embedding failures of single conclusions do not count.</returns>
    private async Task<bool> TryTickAsync(
        LeaseKeeper keeper, IReadOnlyDictionary<WorkTaskType, WorkBatchPolicy> policies, CancellationToken ct)
    {
        try
        {
            await TickAsync(policies, keeper.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception) when (keeper.IsLost && !ct.IsCancellationRequested)
        {
            _metrics.ClearBacklog();
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // The gauges would otherwise keep reporting the last good tick's values while the store is failing.
            _metrics.ClearBacklog();
            LogTickFailed(ex);
            return false;
        }
    }

    private async Task TickAsync(IReadOnlyDictionary<WorkTaskType, WorkBatchPolicy> policies, CancellationToken ct)
    {
        var embedding = _embeddings is null ? new EmbeddingOutcome(0, 0, 0) : await EmbedPendingAsync(_embeddings, ct).ConfigureAwait(false);
        var tombstones = await _store.Conclusions.PurgeTombstonesAsync(_options.TombstoneRetention, ct).ConfigureAwait(false);
        var items = await _store.WorkQueue.PruneSucceededAsync(_options.CompletedRetention, ct).ConfigureAwait(false);
        await RefreshBacklogAsync(policies, ct).ConfigureAwait(false);
        var idempotency = await _store.Idempotency.PurgeExpiredAsync(ct).ConfigureAwait(false);
        var backfilled = await TryBackfillChunksAsync(ct).ConfigureAwait(false);
        LogTick(embedding.Embedded, embedding.Failed, embedding.MarkedFailed, backfilled, tombstones, items, idempotency);
    }

    /// <summary>
    /// Chunks one batch of messages that have none (appended before chunking existed, or while no embedding model was
    /// registered), oldest first, so their chunks join the embedding backlog.
    /// </summary>
    /// <returns>How many messages were chunked.</returns>
    private async Task<int> TryBackfillChunksAsync(CancellationToken ct)
    {
        try
        {
            return await BackfillChunksAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Last, and isolated, so one message the chunker or the store rejects cannot stop the purges and gauges.
            LogBackfillFailed(ex);
            return 0;
        }
    }

    private async Task<int> BackfillChunksAsync(CancellationToken ct)
    {
        if (_chunker is null)
        {
            return 0;
        }

        var messages = await _store.Embeddings.ListUnchunkedMessagesAsync(_options.BackfillBatch, ct).ConfigureAwait(false);
        var chunks = messages
            .SelectMany(message => _chunker.Chunk(message.Content)
                .Select((content, index) => new MessageChunk(message.WorkspaceName, message.SessionName, message.PublicId, index, content)))
            .ToList();
        if (chunks.Count > 0)
        {
            await _store.Embeddings.AddChunksAsync(chunks, ct).ConfigureAwait(false);
        }

        return messages.Count;
    }

    private async Task RefreshBacklogAsync(IReadOnlyDictionary<WorkTaskType, WorkBatchPolicy> policies, CancellationToken ct)
    {
        var backlog = await _store.WorkQueue.GetBacklogAsync(policies, ct).ConfigureAwait(false);
        var conclusionsPending = await _store.Embeddings.CountPendingAsync(PendingEmbeddingKind.Conclusion, ct).ConfigureAwait(false);
        var messagesPending = await _store.Embeddings.CountPendingAsync(PendingEmbeddingKind.MessageChunk, ct).ConfigureAwait(false);
        var oldestAge = backlog.OldestOpenItemCreatedAt is { } oldest
            ? TimeSpan.FromTicks(Math.Max(0, (_clock.GetUtcNow() - oldest).Ticks))
            : TimeSpan.Zero;
        _metrics.RecordBacklog(backlog.EligibleUnits, oldestAge, conclusionsPending, messagesPending);
    }

    /// <summary>
    /// Embeds the backlog, then counts the tick's failures towards <see cref="ReconcilerOptions.EmbeddingMaxAttempts"/>
    /// as the class remarks describe, so neither an outage nor a misconfiguration marks conclusions failed.
    /// </summary>
    private async Task<EmbeddingOutcome> EmbedPendingAsync(IEmbeddingGenerator<string, Embedding<float>> generator, CancellationToken ct)
    {
        var embedded = 0;
        var failures = new List<(PendingEmbedding Pending, Exception Error)>();
        IReadOnlyList<PendingEmbedding> previous = [];
        for (var batch = 0; batch < MaxBatchesPerTick; batch++)
        {
            var pending = await _store.Embeddings.ListPendingAsync(_options.EmbeddingBatchSize, ct).ConfigureAwait(false);
            // A save that did not take (the store kept the conclusion pending) would otherwise repeat one batch for the rest of the tick.
            if (pending.Count == 0 || pending.Select(FailureKey).SequenceEqual(previous.Select(FailureKey)))
            {
                break;
            }

            var failed = failures.Count;
            embedded += await EmbedBatchAsync(generator, pending, failures, ct).ConfigureAwait(false);
            // A failed conclusion stays pending, so keep going only after a clean, full batch.
            if (failures.Count > failed || pending.Count < _options.EmbeddingBatchSize)
            {
                break;
            }

            previous = pending;
        }

        // When every failure is a rejection and nothing was embedded, the cause is more likely the request (a setting the
        // model refuses) than each conclusion, so none counts: a configuration mistake must not fail the whole backlog.
        var everyFailureRejected = failures.All(f => IsRejection(f.Error));
        var counted = failures
            .Where(f => !IsBatchWide(f.Error, ct) && (embedded > 0 || (IsRejection(f.Error) && !everyFailureRejected)))
            .ToList();
        if (counted.Count < failures.Count)
        {
            LogServiceUnavailable(failures.Count - counted.Count);
        }

        TrackFailedTick(embedded, failures, ct);

        var markedFailed = 0;
        foreach (var (pending, _) in counted)
        {
            markedFailed += await RecordFailureAsync(pending, ct).ConfigureAwait(false) ? 1 : 0;
        }

        return new EmbeddingOutcome(embedded, failures.Count, markedFailed);
    }

    /// <summary>Counts consecutive ticks in which embeddings failed and none succeeded, and escalates a long run.</summary>
    private void TrackFailedTick(int embedded, List<(PendingEmbedding Pending, Exception Error)> failures, CancellationToken ct)
    {
        if (embedded > 0 || failures.Count == 0)
        {
            _failedEmbeddingTicks = 0;
            return;
        }

        _failedEmbeddingTicks++;
        if (_failedEmbeddingTicks < _options.FailedEmbeddingTicksBeforeError)
        {
            return;
        }

        var last = failures[^1].Error;
        if (failures.Any(f => ProviderFailure.IsTransient(f.Error, ct)))
        {
            LogEmbeddingUnavailablePersists(last, _failedEmbeddingTicks);
        }
        else
        {
            LogEmbeddingMisconfigured(last, _failedEmbeddingTicks);
        }
    }

    /// <returns>How many conclusions were embedded; the failed ones are added to <paramref name="failures"/>.</returns>
    private async Task<int> EmbedBatchAsync(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        IReadOnlyList<PendingEmbedding> pending,
        List<(PendingEmbedding Pending, Exception Error)> failures,
        CancellationToken ct)
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
                LogEmbeddingFailed(ex, pending[0].Kind, pending[0].SourceId);
                failures.Add((pending[0], ex));
                return 0;
            }

            if (IsBatchWide(ex, ct))
            {
                LogBatchFailedForAll(ex, pending.Count);
                failures.AddRange(pending.Select(item => (item, ex)));
                return 0;
            }

            LogBatchFailed(ex, pending.Count);
            return await EmbedOneByOneAsync(generator, pending, failures, ct).ConfigureAwait(false);
        }

        for (var i = 0; i < pending.Count; i++)
        {
            await SaveAsync(pending[i], vectors[i], ct).ConfigureAwait(false);
        }

        return pending.Count;
    }

    private async Task<int> EmbedOneByOneAsync(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        IReadOnlyList<PendingEmbedding> pending,
        List<(PendingEmbedding Pending, Exception Error)> failures,
        CancellationToken ct)
    {
        var embedded = 0;
        foreach (var item in pending)
        {
            ReadOnlyMemory<float> vector;
            try
            {
                vector = (await GenerateAsync(generator, [item], ct).ConfigureAwait(false))[0];
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                LogEmbeddingFailed(ex, item.Kind, item.SourceId);
                failures.Add((item, ex));
                continue;
            }

            await SaveAsync(item, vector, ct).ConfigureAwait(false);
            embedded++;
        }

        return embedded;
    }

    /// <summary>
    /// True when the service refused this input (HTTP 400, which includes content-filter refusals), as opposed to being
    /// unavailable.
    /// </summary>
    private static bool IsRejection(Exception exception) =>
        ProviderFailure.StatusOf(exception) == (int)HttpStatusCode.BadRequest;

    /// <summary>
    /// True when the failure is not of one input but would fail any: the service failed (a transient failure such as 429,
    /// 503, a gateway error, a timeout or a connection error, but not HTTP 500, which one input can provoke), or the
    /// generator is misconfigured (401 or 403, 404 for a missing deployment, or vectors of the wrong number or size).
    /// Such a batch is not retried one by one, and its failures never count against conclusions.
    /// </summary>
    private static bool IsBatchWide(Exception exception, CancellationToken ct) =>
        (ProviderFailure.IsTransient(exception, ct) &&
            ProviderFailure.StatusOf(exception) != (int)HttpStatusCode.InternalServerError) ||
        ProviderFailure.IsAuthorizationFailure(exception) ||
        ProviderFailure.StatusOf(exception) == (int)HttpStatusCode.NotFound ||
        exception is EmbeddingShapeException;

    /// <exception cref="EmbeddingShapeException">The generator returned the wrong number or size of vectors.</exception>
    private static async Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateAsync(
        IEmbeddingGenerator<string, Embedding<float>> generator, IReadOnlyList<PendingEmbedding> pending, CancellationToken ct)
    {
        var embeddings = await generator.GenerateAsync(pending.Select(p => p.Content), cancellationToken: ct).ConfigureAwait(false);
        if (embeddings.Count != pending.Count || embeddings.Any(e => e.Vector.Length != VectorSpace.Dimensions))
        {
            throw new EmbeddingShapeException(
                $"The embedding generator returned {embeddings.Count} vectors for {pending.Count} conclusions, or vectors " +
                $"that are not {VectorSpace.Dimensions} long. Check the Embeddings configuration.");
        }

        return [.. embeddings.Select(e => e.Vector)];
    }

    private async Task SaveAsync(PendingEmbedding pending, ReadOnlyMemory<float> vector, CancellationToken ct)
    {
        await _store.Embeddings.SaveAsync(pending, vector, _embeddingModel, ct).ConfigureAwait(false);
        _embeddingFailures.Remove(FailureKey(pending));
    }

    private static (PendingEmbeddingKind Kind, string Workspace, string Source, int Chunk) FailureKey(PendingEmbedding pending) =>
        (pending.Kind, pending.WorkspaceName, pending.SourceId, pending.ChunkIndex);

    /// <returns>True when this failure was the last allowed and the conclusion was marked failed.</returns>
    private async Task<bool> RecordFailureAsync(PendingEmbedding pending, CancellationToken ct)
    {
        var key = FailureKey(pending);
        var failures = _embeddingFailures.GetValueOrDefault(key) + 1;
        if (failures < _options.EmbeddingMaxAttempts)
        {
            _embeddingFailures[key] = failures;
            return false;
        }

        await _store.Embeddings.MarkFailedAsync(pending, ct).ConfigureAwait(false);
        _embeddingFailures.Remove(key);
        LogMarkedFailed(pending.Kind, pending.SourceId, pending.WorkspaceName, failures);
        return true;
    }

    private sealed record EmbeddingOutcome(int Embedded, int Failed, int MarkedFailed);

    /// <summary>The generator answered with vectors of the wrong number or size: its configuration, not an input.</summary>
    private sealed class EmbeddingShapeException(string message) : InvalidOperationException(message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No embedding generator is registered; the reconciler will not embed pending conclusions or message chunks.")]
    private partial void LogNoGenerator();

    [LoggerMessage(Level = LogLevel.Information, Message = "{Owner} now holds the reconciler lease.")]
    private partial void LogLeader(string owner);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Taking the reconciler lease failed; retrying next interval.")]
    private partial void LogLeaseFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Releasing the reconciler lease failed; another replica takes over when it expires.")]
    private partial void LogReleaseFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "A reconciler tick failed; the next one is delayed.")]
    private partial void LogTickFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Failures} embedding failures this tick are not counted towards marking items failed: they would fail any input (the service is down or the generator misconfigured), or nothing was embedded.")]
    private partial void LogServiceUnavailable(int failures);

    [LoggerMessage(Level = LogLevel.Error, Message = "Embedding has failed with nothing embedded for {Ticks} reconciler ticks in a row, with transient failures; the embedding service has been unavailable or throttling. Check its health and quota; persistent throttling (429) can also mean Reconciler:EmbeddingBatchSize is too large for the deployment's rate limit.")]
    private partial void LogEmbeddingUnavailablePersists(Exception exception, int ticks);

    [LoggerMessage(Level = LogLevel.Error, Message = "Embedding has failed with nothing embedded for {Ticks} reconciler ticks in a row, and not transiently: likely a misconfiguration. Check the Embeddings endpoint, deployment, dimensions and role assignment.")]
    private partial void LogEmbeddingMisconfigured(Exception exception, int ticks);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Embedding a batch of {Count} items failed; embedding them one by one.")]
    private partial void LogBatchFailed(Exception exception, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Embedding a batch of {Count} items failed in a way that would fail any input (the service is throttled or unavailable, or the generator is misconfigured); retrying them next tick, not one by one.")]
    private partial void LogBatchFailedForAll(Exception exception, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Embedding {Kind} {SourceId} failed.")]
    private partial void LogEmbeddingFailed(Exception exception, PendingEmbeddingKind kind, string sourceId);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Kind} {SourceId} in workspace {WorkspaceName} failed to embed {Failures} times; marked failed, it is invisible to semantic reads until its content changes.")]
    private partial void LogMarkedFailed(PendingEmbeddingKind kind, string sourceId, string workspaceName, int failures);

    [LoggerMessage(Level = LogLevel.Error, Message = "Chunking unchunked messages failed; the other reconciler steps ran, and the backfill is retried next tick.")]
    private partial void LogBackfillFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Reconciler tick: embedded {Embedded}, {Failed} embedding failures ({MarkedFailed} marked failed), chunked {Backfilled} unchunked messages, purged {Tombstones} tombstones, pruned {Items} work items and {Idempotency} idempotency records.")]
    private partial void LogTick(int embedded, int failed, int markedFailed, int backfilled, int tombstones, int items, int idempotency);
}
