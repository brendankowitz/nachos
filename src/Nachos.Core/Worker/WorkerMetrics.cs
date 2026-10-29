using System.Diagnostics.Metrics;

namespace Nachos.Core.Worker;

/// <summary>The worker's instruments (spec §19), on the <see cref="MeterName"/> meter.</summary>
/// <remarks>
/// The meter's <see cref="Meter.Scope"/> is this instance, so a listener can tell the instruments of two containers in
/// one process apart.
/// </remarks>
public sealed class WorkerMetrics : IDisposable
{
    /// <summary>The meter name; hosts add it as a metrics source.</summary>
    public const string MeterName = "Nachos.Worker";

    private readonly Meter _meter;
    private readonly Counter<long> _deadLettered;
    private readonly Counter<long> _facts;
    private readonly Histogram<double> _dedupRatio;

    public WorkerMetrics()
    {
        _meter = new Meter(new MeterOptions(MeterName) { Scope = this });
        _deadLettered = _meter.CreateCounter<long>(
            "nachos.queue.dead_lettered", "{item}", "Work items dead-lettered after their last attempt.");
        _facts = _meter.CreateCounter<long>(
            "nachos.deriver.facts", "{fact}", "Valid facts the Deriver extracted, counted once per committed batch.");
        _dedupRatio = _meter.CreateHistogram<double>(
            "nachos.dedup.ratio", null, "Per committed batch, the share of observer facts that matched an existing or earlier fact instead of inserting.");
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

    public void Dispose() => _meter.Dispose();
}
