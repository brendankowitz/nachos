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
    /// delay jittered by ±20%; must be positive, since transient failures retry indefinitely with this backoff; default
    /// 30 seconds.
    /// </summary>
    /// <remarks>
    /// For failures that consume an attempt, with the default <see cref="Configuration.DeriverOptions.MaxAttempts"/> of 5,
    /// the four retries wait 30 s, 1, 2 and 4 minutes before the unit is dead-lettered. Transient model-provider failures
    /// (408, 429, 5xx, connection errors, timeouts) consume no attempt and back off the same way, so an outage of any
    /// length never dead-letters work.
    /// </remarks>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The longest retry delay; default 10 minutes.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Consecutive transient model-provider failures of one unit from which each further one is logged as an error rather
    /// than a warning; the unit keeps retrying and is never dead-lettered for them. Default 10, about an hour of backoff
    /// with the default delays.
    /// </summary>
    public int TransientFailuresBeforeError { get; set; } = 10;
}
