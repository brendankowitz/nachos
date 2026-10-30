namespace Nachos.Core.Worker;

/// <summary>
/// Queue-consumer settings, bound from <c>Worker</c>. Attempts and lease length are the Deriver's
/// (<see cref="Configuration.DeriverOptions.MaxAttempts"/>, <see cref="Configuration.DeriverOptions.LeaseDuration"/>).
/// </summary>
public sealed class WorkerOptions
{
    /// <summary>How many units are processed at once by this process; default 4.</summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>The first wait after finding no due work; doubles while idle up to <see cref="MaxIdleDelay"/>; default 1 second.</summary>
    public TimeSpan IdleDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest wait between polls while idle; default 15 seconds.</summary>
    public TimeSpan MaxIdleDelay { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The retry delay after a unit's first failed attempt; doubles per attempt up to <see cref="MaxRetryDelay"/>, each
    /// delay jittered by ±20%; default 30 seconds.
    /// </summary>
    /// <remarks>
    /// This sets how long an outage a unit survives before it is dead-lettered: with the default
    /// <see cref="Configuration.DeriverOptions.MaxAttempts"/> of 5, the four retries wait 30 s, 1, 2 and 4 minutes, about
    /// 7.5 minutes in all. Every failure counts the same; M2 does not tell transient errors from permanent ones.
    /// </remarks>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The longest retry delay; default 10 minutes.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(10);
}
