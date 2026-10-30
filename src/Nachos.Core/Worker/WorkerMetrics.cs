using System.Diagnostics.Metrics;

namespace Nachos.Core.Worker;

/// <summary>The worker's instruments (spec §19), on the <see cref="MeterName"/> meter.</summary>
/// <remarks>
/// <para>
/// The meter's <see cref="Meter.Scope"/> is this instance, so a listener can tell the instruments of two containers in
/// one process apart.
/// </para>
/// <para>
/// The backlog gauges (<c>nachos.queue.eligible_units</c>, <c>nachos.queue.oldest_age</c>,
/// <c>nachos.conclusions.embedding_pending</c>) are cluster-wide values that the reconciler refreshes once per tick.
/// Only the replica holding the reconciler lease reports them, so summing or taking the maximum across replicas gives
/// the same answer; the others report nothing.
/// </para>
/// </remarks>
public sealed class WorkerMetrics : IDisposable
{
    /// <summary>The meter name; hosts add it as a metrics source.</summary>
    public const string MeterName = "Nachos.Worker";

    private readonly Meter _meter;
    private readonly Counter<long> _deadLettered;
    private readonly Counter<long> _facts;
    private readonly Histogram<double> _dedupRatio;
    private Backlog? _backlog;

    public WorkerMetrics()
    {
        _meter = new Meter(new MeterOptions(MeterName) { Scope = this });
        _deadLettered = _meter.CreateCounter<long>(
            "nachos.queue.dead_lettered", "{item}", "Work items dead-lettered after their last attempt.");
        _facts = _meter.CreateCounter<long>(
            "nachos.deriver.facts", "{fact}", "Valid facts the Deriver extracted, counted once per committed batch.");
        _dedupRatio = _meter.CreateHistogram<double>(
            "nachos.dedup.ratio", null, "Per committed batch, the share of observer facts that matched an existing or earlier fact instead of inserting.");
        _meter.CreateObservableGauge(
            "nachos.queue.eligible_units",
            () => Observe(backlog => backlog.EligibleUnits),
            "{unit}",
            "Work units due now and not leased, across all workspaces, as of the last reconciler tick.");
        _meter.CreateObservableGauge(
            "nachos.queue.oldest_age",
            () => Observe(backlog => backlog.OldestAge.TotalSeconds),
            "s",
            "Age of the oldest unfinished work item, across all workspaces, as of the last reconciler tick; 0 when there is none.");
        _meter.CreateObservableGauge(
            "nachos.conclusions.embedding_pending",
            () => Observe(backlog => backlog.EmbeddingPending),
            "{conclusion}",
            "Live conclusions waiting for an embedding, across all workspaces, as of the last reconciler tick.");
    }

    public void RecordDeadLettered(int items)
    {
        if (items > 0)
        {
            _deadLettered.Add(items);
        }
    }

    /// <param name="facts">Valid facts extracted from the batch.</param>
    /// <param name="observerFacts">Facts summed over the observers each one reached.</param>
    /// <param name="inserts">Conclusions the batch inserted.</param>
    public void RecordDerivation(int facts, int observerFacts, int inserts)
    {
        _facts.Add(facts);
        if (observerFacts > 0)
        {
            _dedupRatio.Record((double)(observerFacts - inserts) / observerFacts);
        }
    }

    /// <summary>Publishes the backlog gauges' values; called by the reconciler lease holder.</summary>
    public void RecordBacklog(long eligibleUnits, TimeSpan oldestAge, long embeddingPending) =>
        Volatile.Write(ref _backlog, new Backlog(eligibleUnits, oldestAge, embeddingPending));

    /// <summary>Stops reporting the backlog gauges, when this replica no longer holds the reconciler lease.</summary>
    public void ClearBacklog() => Volatile.Write(ref _backlog, null);

    public void Dispose() => _meter.Dispose();

    private IEnumerable<Measurement<T>> Observe<T>(Func<Backlog, T> value)
        where T : struct =>
        Volatile.Read(ref _backlog) is { } backlog ? [new Measurement<T>(value(backlog))] : [];

    private sealed record Backlog(long EligibleUnits, TimeSpan OldestAge, long EmbeddingPending);
}
