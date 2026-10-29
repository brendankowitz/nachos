using Microsoft.Extensions.Logging;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;

namespace Nachos.Core.Worker;

/// <summary>
/// Renews one lease every third of its duration until disposed. When a renewal finds the lease lost, <see cref="Token"/>
/// is cancelled so the work running under the lease stops early; its writes would be rejected anyway.
/// </summary>
/// <remarks>
/// A renewal that fails for another reason (the store is briefly unavailable) is logged and retried at the next
/// interval: the lease still has two thirds of its duration left.
/// </remarks>
internal sealed partial class LeaseKeeper : IAsyncDisposable
{
    private readonly CancellationTokenSource _work;
    private readonly CancellationTokenSource _stop;
    private Task _loop = Task.CompletedTask;
    private volatile bool _isLost;

    private LeaseKeeper(CancellationToken ct)
    {
        _work = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
    }

    /// <summary>Cancelled when the lease is lost or <c>ct</c> passed to <see cref="Start"/> is cancelled.</summary>
    public CancellationToken Token => _work.Token;

    /// <summary>True once a renewal has found the lease no longer held.</summary>
    public bool IsLost => _isLost;

    /// <summary>Starts renewing <paramref name="lease"/>; the first renewal is due <paramref name="duration"/> / 3 from now.</summary>
    public static LeaseKeeper Start(
        IWorkQueue queue, WorkLease lease, TimeSpan duration, TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        var keeper = new LeaseKeeper(ct);
        keeper._loop = keeper.RenewAsync(queue, lease, duration, clock, logger, keeper._stop.Token);
        return keeper;
    }

    /// <summary>Stops renewing and waits for an in-flight renewal, so no renewal overlaps what the caller does next.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        _stop.Dispose();
        _work.Dispose();
    }

    private async Task RenewAsync(
        IWorkQueue queue, WorkLease lease, TimeSpan duration, TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        var interval = duration / 3;
        try
        {
            while (true)
            {
                await Task.Delay(interval, clock, ct).ConfigureAwait(false);
                try
                {
                    await queue.RenewAsync(lease, duration, ct).ConfigureAwait(false);
                }
                catch (LeaseLostException)
                {
                    _isLost = true;
                    LogLeaseLost(logger, lease.WorkUnitKey);
                    await _work.CancelAsync().ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    LogRenewalFailed(logger, ex, lease.WorkUnitKey);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The lease on work unit {WorkUnitKey} was lost to another owner; stopping its work.")]
    private static partial void LogLeaseLost(ILogger logger, string workUnitKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Renewing the lease on work unit {WorkUnitKey} failed; retrying at the next interval.")]
    private static partial void LogRenewalFailed(ILogger logger, Exception exception, string workUnitKey);
}
