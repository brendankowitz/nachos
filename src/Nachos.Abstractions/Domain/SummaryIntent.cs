namespace Nachos.Abstractions.Domain;

/// <summary>
/// The session's resolved summary configuration, passed with an append so the store, which allocates the <c>Seq</c>
/// values, can enqueue the <see cref="WorkTaskType.Summary"/> items whose threshold the batch crosses.
/// </summary>
/// <param name="Enabled">False enqueues nothing.</param>
/// <param name="ShortEvery">A short summary is due at every multiple of this <c>Seq</c>; at least 1.</param>
/// <param name="LongEvery">A long summary is due at every multiple of this <c>Seq</c>; at least 1.</param>
/// <exception cref="ArgumentOutOfRangeException">An interval is less than 1.</exception>
public sealed record SummaryIntent(bool Enabled, int ShortEvery, int LongEvery)
{
    public int ShortEvery { get; init => field = Positive(value); } = Positive(ShortEvery);

    public int LongEvery { get; init => field = Positive(value); } = Positive(LongEvery);

    /// <summary>
    /// The highest multiple of the kind's interval in <c>[firstSeq, lastSeq]</c>: the <c>through_seq</c> of the one item a
    /// batch with those <c>Seq</c> values enqueues for the kind. Null when the batch crosses no multiple, or when
    /// summaries are disabled.
    /// </summary>
    public long? DueThrough(SummaryKind kind, long firstSeq, long lastSeq)
    {
        if (!Enabled)
        {
            return null;
        }

        long every = kind == SummaryKind.Short ? ShortEvery : LongEvery;
        var through = lastSeq / every * every;
        return through >= firstSeq && through > 0 ? through : null;
    }

    private static int Positive(int value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
        return value;
    }
}
