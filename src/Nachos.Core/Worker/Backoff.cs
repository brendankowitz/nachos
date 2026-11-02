namespace Nachos.Core.Worker;

/// <summary>Exponential backoff with jitter, so replicas that failed or went idle together do not retry together.</summary>
internal static class Backoff
{
    /// <summary>The largest fraction a delay is moved by, either way.</summary>
    public const double Jitter = 0.2;

    /// <summary>
    /// <paramref name="initial"/> doubled for each step after the first, capped at <paramref name="max"/>, then scaled by a
    /// random factor in [1 - <see cref="Jitter"/>, 1 + <see cref="Jitter"/>).
    /// </summary>
    /// <param name="step">1 for the first delay.</param>
    public static TimeSpan Delay(TimeSpan initial, TimeSpan max, int step)
    {
        var doublings = Math.Clamp(step - 1, 0, 30);
        var ticks = Math.Min((double)initial.Ticks * (1L << doublings), max.Ticks);
        return TimeSpan.FromTicks((long)(ticks * (1 - Jitter + (2 * Jitter * Random.Shared.NextDouble()))));
    }
}
