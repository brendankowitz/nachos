using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Core.Configuration;
using Nachos.Core.Llm;

namespace Nachos.Core.Worker;

/// <summary>
/// Runs <see cref="WorkerOptions.MaxConcurrency"/> loops that lease due units of the task types that have a registered
/// <see cref="IWorkHandler"/>, and run that handler on each, in a DI scope of its own.
/// </summary>
/// <remarks>
/// <para>
/// While a handler runs its lease is renewed every third of <see cref="DeriverOptions.LeaseDuration"/>. A handler that
/// returns has completed the unit. One that throws has its exception logged here, once, and the lease failed with an
/// exponential, jittered retry delay; items on their <see cref="DeriverOptions.MaxAttempts"/>th attempt are dead-lettered.
/// A lost lease (<see cref="LeaseLostException"/>) ends the unit without failing it, since it has a new owner.
/// </para>
/// <para>
/// A transient failure is not the unit's fault, so it consumes no attempt: the unit is released
/// (<see cref="IWorkQueue.ReleaseAsync"/>) with the attempt given back and the same backoff, stepped by the unit's
/// consecutive transient failures. Transient are a model-provider failure that retrying can fix (HTTP 408, 429 or 5xx, a
/// connection error or a provider timeout), a store failure the store reports as transient
/// (<see cref="IMemoryStore.IsTransientFailure"/>: a deadlock, a database failover), and a model-provider authorization
/// failure (HTTP 401 or 403, a token that cannot be acquired). The last is usually a misconfiguration, but it is also what
/// a new role assignment returns until it propagates, and dead-lettering work for that would lose it. An outage therefore
/// never dead-letters work; from the <see cref="WorkerOptions.TransientFailuresBeforeError"/>th consecutive one on, each
/// is logged as an error, so a configuration that only ever fails this way (a wrong endpoint, a missing role) still
/// surfaces. The counts are per process, reset when the unit ends any other way, and are forgotten for a unit not seen
/// again within three <see cref="WorkerOptions.MaxRetryDelay"/>s (another replica took it over).
/// </para>
/// <para>
/// Attempts are counted when a unit is leased, so a worker that dies mid-unit still uses one up. A unit leased with an
/// item already past <see cref="DeriverOptions.MaxAttempts"/> (its workers kept dying) is dead-lettered without running.
/// </para>
/// <para>
/// On shutdown a running handler is cancelled and its unit released (<see cref="IWorkQueue.ReleaseAsync"/>): claimable
/// at once by another replica, and with the interrupted attempt given back, so a unit on its last attempt is not
/// dead-lettered by a restart.
/// </para>
/// <para>
/// An unexpected exception while processing a unit (outside the handler) is logged and the loop carries on after an
/// idle delay, so one bad unit cannot stop a loop or the host.
/// </para>
/// </remarks>
public sealed partial class QueueConsumerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IMemoryStore _store;
    private readonly TimeProvider _clock;
    private readonly WorkerOptions _options;
    private readonly DeriverOptions _deriver;
    private readonly WorkerMetrics _metrics;
    private readonly ILogger<QueueConsumerService> _logger;
    private readonly string _instance = Guid.NewGuid().ToString("N")[..8];
    private readonly ConcurrentDictionary<string, TransientStreak> _transientFailures = new(StringComparer.Ordinal);

    public QueueConsumerService(
        IServiceScopeFactory scopes,
        IMemoryStore store,
        TimeProvider clock,
        IOptions<WorkerOptions> options,
        IOptions<NachosOptions> nachosOptions,
        WorkerMetrics metrics,
        ILogger<QueueConsumerService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(nachosOptions);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);
        _scopes = scopes;
        _store = store;
        _clock = clock;
        _options = options.Value;
        _deriver = nachosOptions.Value.Deriver;
        _metrics = metrics;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var policies = await WorkHandlerPolicies.ReadAsync(_scopes).ConfigureAwait(false);
        if (policies.Count == 0)
        {
            LogNoHandlers();
            return;
        }

        var taskTypes = string.Join(", ", policies.Keys);
        LogStarted(_options.MaxConcurrency, taskTypes);
        // Task.Run: a synchronous store (in-memory) would otherwise run the first loop inline, inside host startup.
        var loops = Enumerable.Range(1, _options.MaxConcurrency)
            .Select(loop => Task.Run(() => RunLoopAsync(Owner(loop), policies, stoppingToken), CancellationToken.None));
        await Task.WhenAll(loops).ConfigureAwait(false);
    }

    /// <summary>Unique per loop and per service instance, so leases and logs name the loop that holds them.</summary>
    private string Owner(int loop)
    {
        var owner = $"{Environment.MachineName}:{Environment.ProcessId}:{_instance}:{loop}";
        return owner.Length <= WorkLease.MaxOwnerLength ? owner : owner[^WorkLease.MaxOwnerLength..];
    }

    private async Task RunLoopAsync(string owner, Dictionary<WorkTaskType, WorkBatchPolicy> policies, CancellationToken ct)
    {
        var idle = 0;
        while (!ct.IsCancellationRequested)
        {
            WorkLease? lease;
            try
            {
                lease = await _store.WorkQueue.TryAcquireAsync(owner, policies, _deriver.LeaseDuration, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The store is unavailable: wait as if idle rather than spin.
                LogAcquireFailed(ex, owner);
                lease = null;
            }

            if (lease is null)
            {
                idle++;
                if (!await DelayAsync(Backoff.Delay(_options.IdleDelay, _options.MaxIdleDelay, idle), ct).ConfigureAwait(false))
                {
                    return;
                }

                continue;
            }

            idle = 0;
            try
            {
                await ProcessAsync(lease, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                LogProcessFailed(ex, lease.WorkUnitKey);
                if (!await DelayAsync(Backoff.Delay(_options.IdleDelay, _options.MaxIdleDelay, 1), ct).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
    }

    private async Task ProcessAsync(WorkLease lease, CancellationToken ct)
    {
        var attempts = lease.Items.Max(item => item.Attempts);
        if (attempts > _deriver.MaxAttempts)
        {
            _transientFailures.TryRemove(lease.WorkUnitKey, out _);
            LogAbandoned(lease.WorkUnitKey, attempts);
            await FailAsync(
                lease,
                $"Attempts exhausted ({attempts - 1}; the last attempts ended without a result).",
                TimeSpan.Zero,
                _deriver.MaxAttempts).ConfigureAwait(false);
            return;
        }

        // Null after the block below only when the handler was interrupted by shutdown.
        Exception? failure = null;
        var keeper = LeaseKeeper.Start(_store.WorkQueue, lease, _deriver.LeaseDuration, _clock, _logger, ct);
        await using (keeper.ConfigureAwait(false))
        {
            try
            {
                await HandleAsync(lease, keeper.Token).ConfigureAwait(false);
                _transientFailures.TryRemove(lease.WorkUnitKey, out _);
                return;
            }
            catch (LeaseLostException)
            {
                _transientFailures.TryRemove(lease.WorkUnitKey, out _);
                LogLeaseLost(lease.WorkUnitKey);
                return;
            }
            catch (Exception ex) when (keeper.IsLost)
            {
                _transientFailures.TryRemove(lease.WorkUnitKey, out _);
                if (ex is OperationCanceledException)
                {
                    LogStoppedAfterLeaseLost(ex, lease.WorkUnitKey);
                }
                else
                {
                    LogFailedAfterLeaseLost(ex, lease.WorkUnitKey);
                }

                return;
            }
            catch (Exception ex) when (ct.IsCancellationRequested)
            {
                LogInterrupted(ex, lease.WorkUnitKey);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }

        // The keeper is stopped by now, so no renewal races the release.
        if (failure is null)
        {
            await ReleaseAsync(lease, TimeSpan.Zero).ConfigureAwait(false);
            return;
        }

        if (IsTransient(failure, ct))
        {
            var streak = RecordTransientFailure(lease.WorkUnitKey);
            var retryDelay = Backoff.Delay(_options.RetryDelay, _options.MaxRetryDelay, streak);
            if (streak >= _options.TransientFailuresBeforeError)
            {
                LogTransientFailurePersists(failure, lease.WorkUnitKey, streak, retryDelay);
            }
            else
            {
                LogTransientFailure(failure, lease.WorkUnitKey, streak, retryDelay);
            }

            await ReleaseAsync(lease, retryDelay).ConfigureAwait(false);
            return;
        }

        _transientFailures.TryRemove(lease.WorkUnitKey, out _);
        var deadLettered = lease.Items.Count(item => item.Attempts >= _deriver.MaxAttempts);
        var delay = Backoff.Delay(_options.RetryDelay, _options.MaxRetryDelay, attempts);
        if (deadLettered > 0)
        {
            LogHandlerFailedFinal(failure, lease.WorkUnitKey, attempts, deadLettered);
        }
        else
        {
            LogHandlerFailed(failure, lease.WorkUnitKey, attempts, delay);
        }

        await FailAsync(lease, Describe(failure), delay, _deriver.MaxAttempts).ConfigureAwait(false);
    }

    private bool IsTransient(Exception failure, CancellationToken ct) =>
        ProviderFailure.IsTransient(failure, ct) || ProviderFailure.IsAuthorizationFailure(failure) || _store.IsTransientFailure(failure);

    /// <summary>
    /// Counts one more consecutive transient failure of the unit, and forgets every unit not seen for
    /// <see cref="StreakExpiry"/>, so units that another replica finished cannot pile up here.
    /// </summary>
    /// <returns>The unit's streak, this failure included.</returns>
    private int RecordTransientFailure(string workUnitKey)
    {
        var now = _clock.GetUtcNow();
        var expiry = StreakExpiry;
        foreach (var entry in _transientFailures)
        {
            if (now - entry.Value.LastFailedAt > expiry)
            {
                _transientFailures.TryRemove(entry);
            }
        }

        return _transientFailures.AddOrUpdate(
            workUnitKey,
            new TransientStreak(1, now),
            (_, streak) => new TransientStreak(now - streak.LastFailedAt > expiry ? 1 : streak.Count + 1, now)).Count;
    }

    /// <summary>
    /// A released unit is due again within <see cref="WorkerOptions.MaxRetryDelay"/> (plus jitter), and its next run may
    /// last up to a lease before failing again; one not seen for three retry delays plus a lease was taken over by another
    /// replica, so its streak here is stale.
    /// </summary>
    private TimeSpan StreakExpiry => (_options.MaxRetryDelay * 3) + _deriver.LeaseDuration;

    private async Task HandleAsync(WorkLease lease, CancellationToken ct)
    {
        var taskType = lease.Items[0].TaskType;
        var scope = _scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var handler = scope.ServiceProvider.GetServices<IWorkHandler>().Single(h => h.TaskType == taskType);
            await handler.HandleAsync(lease, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Fails the lease; a failure to fail is logged, and the lease then expires and the unit is reclaimed.</summary>
    private async Task FailAsync(WorkLease lease, string reason, TimeSpan retryDelay, int maxAttempts)
    {
        try
        {
            // CancellationToken.None, as in ReleaseAsync: on shutdown the stopping token may already be cancelled, and a
            // failure not recorded would skip its retry delay. The host's shutdown timeout bounds this.
            await _store.WorkQueue.FailAsync(lease, reason, retryDelay, maxAttempts, CancellationToken.None).ConfigureAwait(false);
        }
        catch (LeaseLostException)
        {
            LogLeaseLost(lease.WorkUnitKey);
            return;
        }
        catch (Exception ex)
        {
            LogFailFailed(ex, lease.WorkUnitKey);
            return;
        }

        _metrics.RecordDeadLettered(lease.Items.Count(item => item.Attempts >= maxAttempts));
    }

    /// <summary>
    /// Releases a unit without consuming an attempt; if that fails, the lease expires and the unit is reclaimed (the
    /// attempt then counts).
    /// </summary>
    private async Task ReleaseAsync(WorkLease lease, TimeSpan retryDelay)
    {
        try
        {
            // CancellationToken.None: on shutdown the stopping token is already cancelled, and the host's shutdown
            // timeout bounds this.
            await _store.WorkQueue.ReleaseAsync(lease, retryDelay, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogReleaseFailed(ex, lease.WorkUnitKey);
        }
    }

    private readonly record struct TransientStreak(int Count, DateTimeOffset LastFailedAt);

    private static string Describe(Exception exception) => $"{exception.GetType().FullName}: {exception.Message}";

    /// <returns>False when <paramref name="ct"/> was cancelled.</returns>
    private async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, _clock, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Queue consumer started {Loops} loops for task types {TaskTypes}.")]
    private partial void LogStarted(int loops, string taskTypes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No IWorkHandler is registered; the queue consumer is not running.")]
    private partial void LogNoHandlers();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Leasing work for {Owner} failed; retrying after the idle delay.")]
    private partial void LogAcquireFailed(Exception exception, string owner);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Work unit {WorkUnitKey} failed on attempt {Attempt}; retrying in {RetryDelay}.")]
    private partial void LogHandlerFailed(Exception exception, string workUnitKey, int attempt, TimeSpan retryDelay);

    [LoggerMessage(Level = LogLevel.Error, Message = "Work unit {WorkUnitKey} failed on attempt {Attempt}, the last for {DeadLettered} of its items; dead-lettering them.")]
    private partial void LogHandlerFailedFinal(Exception exception, string workUnitKey, int attempt, int deadLettered);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The lease on work unit {WorkUnitKey} was lost; it has a new owner, so it is not failed.")]
    private partial void LogLeaseLost(string workUnitKey);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failing the lease on work unit {WorkUnitKey} failed; the unit is retried when the lease expires.")]
    private partial void LogFailFailed(Exception exception, string workUnitKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Work unit {WorkUnitKey} stopped after its lease was lost.")]
    private partial void LogStoppedAfterLeaseLost(Exception exception, string workUnitKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Work unit {WorkUnitKey} failed after its lease was lost; it has a new owner, so it is not failed.")]
    private partial void LogFailedAfterLeaseLost(Exception exception, string workUnitKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "Work unit {WorkUnitKey} was interrupted by shutdown; releasing it for another worker.")]
    private partial void LogInterrupted(Exception exception, string workUnitKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Work unit {WorkUnitKey} hit a transient failure ({Streak} in a row); released without using an attempt, retrying in {RetryDelay}.")]
    private partial void LogTransientFailure(Exception exception, string workUnitKey, int streak, TimeSpan retryDelay);

    [LoggerMessage(Level = LogLevel.Error, Message = "Work unit {WorkUnitKey} hit transient failure number {Streak} in a row; released without using an attempt, retrying in {RetryDelay}. Check the model endpoint, deployments, quota and role assignments, and the database.")]
    private partial void LogTransientFailurePersists(Exception exception, string workUnitKey, int streak, TimeSpan retryDelay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Releasing work unit {WorkUnitKey} failed; it is retried when its lease expires.")]
    private partial void LogReleaseFailed(Exception exception, string workUnitKey);

    [LoggerMessage(Level = LogLevel.Error, Message = "Processing work unit {WorkUnitKey} failed unexpectedly; the loop continues.")]
    private partial void LogProcessFailed(Exception exception, string workUnitKey);

    [LoggerMessage(Level = LogLevel.Error, Message = "Work unit {WorkUnitKey} was leased for attempt {Attempt}, past the maximum: attempts exhausted (the last attempts ended without a result). Dead-lettering it.")]
    private partial void LogAbandoned(string workUnitKey, int attempt);
}
