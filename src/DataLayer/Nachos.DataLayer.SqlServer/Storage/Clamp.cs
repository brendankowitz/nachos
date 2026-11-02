namespace Nachos.DataLayer.SqlServer.Storage;

/// <summary>
/// Time arithmetic that saturates instead of throwing: a caller's retention, back-off or lease length may be as large as
/// <see cref="TimeSpan.MaxValue"/> (or as small as <see cref="TimeSpan.MinValue"/>), which then means "never" or "always"
/// rather than an overflow.
/// </summary>
internal static class Clamp
{
    public static DateTimeOffset Add(DateTimeOffset time, TimeSpan span)
    {
        var ticks = time.UtcTicks;
        return span.Ticks > DateTimeOffset.MaxValue.UtcTicks - ticks ? DateTimeOffset.MaxValue
            : span.Ticks < DateTimeOffset.MinValue.UtcTicks - ticks ? DateTimeOffset.MinValue
            : time + span;
    }

    public static DateTimeOffset Subtract(DateTimeOffset time, TimeSpan span) =>
        span == TimeSpan.MinValue ? DateTimeOffset.MaxValue : Add(time, span.Negate());
}
