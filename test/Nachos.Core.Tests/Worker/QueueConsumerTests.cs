using System.Diagnostics.Metrics;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Core.Worker;
using Shouldly;

namespace Nachos.Core.Tests.Worker;

public sealed class QueueConsumerTests
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ProcessesAndCompletes()
    {
        await using var fixture = new WorkerFixture();
        await fixture.SeedAsync();
        var first = await fixture.SayAsync("one");
        var second = await fixture.SayAsync("two");

        await fixture.StartConsumerAsync();
        await Pump.UntilAsync(fixture.Clock, async () => (await fixture.QueueAsync()).Completed == 2, Step);

        var handler = fixture.Handler!;
        handler.Calls.Select(c => c.Lease.Items.Single().MessageId).ShouldBe([first.PublicId, second.PublicId]);
        (await fixture.QueueAsync()).ShouldBe(new QueueCounts(0, 0, 2, 0));
        fixture.Store.Failures.ShouldBeEmpty();
    }

    [Fact]
    public async Task HandlerThrows_FailsWithBackoff()
    {
        var retryDelay = TimeSpan.FromSeconds(10);
        await using var fixture = new WorkerFixture(configureWorker: options => options.RetryDelay = retryDelay);
        await fixture.SeedAsync();
        await fixture.SayAsync("one");
        var handler = fixture.Handler!;
        var calls = 0;
        handler.Behavior = (lease, ct) => Interlocked.Increment(ref calls) == 1
            ? throw new InvalidOperationException("model unavailable")
            : fixture.Store.WorkQueue.CompleteAsync(lease, ct);

        await fixture.StartConsumerAsync();
        await Pump.UntilAsync(fixture.Clock, () => fixture.Store.Failures.Count == 1, Step);
        (await fixture.QueueAsync()).ShouldBe(new QueueCounts(1, 0, 0, 0));
        // Released for retry, not claimable before its delay: a competitor finds nothing due.
        (await fixture.CompeteAsync()).ShouldBeNull();

        await Pump.UntilAsync(fixture.Clock, async () => (await fixture.QueueAsync()).Completed == 1, Step);
        var (failed, failedAt) = handler.Calls.First();
        var (retried, retriedAt) = handler.Calls.Last();
        failed.Items.Single().Attempts.ShouldBe(1);
        retried.Items.Single().Attempts.ShouldBe(2);
        // First retry: the base delay with at most 20% jitter, then at most one idle poll (1 s, jittered) and one step.
        (retriedAt - failedAt).ShouldBeInRange(retryDelay * (1 - Backoff.Jitter), (retryDelay * (1 + Backoff.Jitter)) + TimeSpan.FromSeconds(1.2) + Step);
    }

    [Fact]
    public async Task DeadLettersAfterMaxAttempts()
    {
        await using var fixture = new WorkerFixture(configureNachos: options => options.Deriver.MaxAttempts = 3);
        await fixture.SeedAsync();
        await fixture.SayAsync("one");
        fixture.Handler!.Behavior = (_, _) => throw new InvalidOperationException("always broken");
        using var deadLettered = new MeasurementRecorder(fixture.Metrics, "nachos.queue.dead_lettered");

        await fixture.StartConsumerAsync();
        await Pump.UntilAsync(fixture.Clock, async () => (await fixture.QueueAsync()).DeadLettered == 1, TimeSpan.FromSeconds(5));

        fixture.Handler.Calls.Select(c => c.Lease.Items.Single().Attempts).ShouldBe([1, 2, 3]);
        (await fixture.QueueAsync()).ShouldBe(new QueueCounts(0, 0, 0, 1));
        deadLettered.Total.ShouldBe(1);
    }

    [Fact]
    public async Task DeadLettersAUnitWhoseWorkersKeptDying_WithoutRunningIt()
    {
        await using var fixture = new WorkerFixture(configureNachos: options => options.Deriver.MaxAttempts = 2);
        await fixture.SeedAsync();
        await fixture.SayAsync("poison");
        // Two workers lease the unit and die: each lease counts an attempt and then expires unreleased.
        for (var crash = 0; crash < 2; crash++)
        {
            (await fixture.CompeteAsync($"crashed-{crash}")).ShouldNotBeNull();
            fixture.Clock.Advance(WorkerFixture.Lease + TimeSpan.FromTicks(1));
        }

        await fixture.StartConsumerAsync();
        await Pump.UntilAsync(fixture.Clock, async () => (await fixture.QueueAsync()).DeadLettered == 1, Step);

        fixture.Handler!.Calls.ShouldBeEmpty();
        fixture.Store.Failures.Single().Items.Single().Attempts.ShouldBe(3);
    }

    [Fact]
    public async Task LostLease_EndsTheUnitWithoutFailingIt()
    {
        await using var fixture = new WorkerFixture();
        await fixture.SeedAsync();
        await fixture.SayAsync("one");
        var handler = fixture.Handler!;
        handler.Behavior = (lease, ct) => handler.Calls.Count == 1
            ? throw new LeaseLostException(lease.WorkUnitKey)
            : fixture.Store.WorkQueue.CompleteAsync(lease, ct);

        await fixture.StartConsumerAsync();
        await Pump.UntilAsync(fixture.Clock, () => handler.Calls.Count == 1, Step);

        // Not failed: the unit stays leased until the lease expires, then runs again.
        (await fixture.QueueAsync()).ShouldBe(new QueueCounts(0, 1, 0, 0));
        await Pump.UntilAsync(fixture.Clock, async () => (await fixture.QueueAsync()).Completed == 1, TimeSpan.FromSeconds(30));
        fixture.Store.Failures.ShouldBeEmpty();
        handler.Calls.Last().Lease.Items.Single().Attempts.ShouldBe(2);
    }

    [Fact]
    public async Task RenewsLongRunningLease()
    {
        await using var fixture = new WorkerFixture();
        await fixture.SeedAsync();
        await fixture.SayAsync("one");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler!.Behavior = async (lease, ct) =>
        {
            started.SetResult();
            await release.Task.WaitAsync(ct);
            await fixture.Store.WorkQueue.CompleteAsync(lease, ct);
        };

        await fixture.StartConsumerAsync();
        await Pump.UntilAsync(fixture.Clock, () => started.Task.IsCompleted, Step);
        var leasedAt = fixture.Clock.GetUtcNow();

        fixture.Clock.Advance(WorkerFixture.Lease / 3);
        await Pump.UntilAsync(fixture.Clock, () => fixture.Store.Renewals >= 1, TimeSpan.Zero);
        fixture.Clock.SetUtcNow(leasedAt + WorkerFixture.Lease + TimeSpan.FromTicks(1));

        // Past the original expiry the unit is still held, so a competitor cannot take it.
        (await fixture.CompeteAsync()).ShouldBeNull();
        release.SetResult();
        await Pump.UntilAsync(fixture.Clock, async () => (await fixture.QueueAsync()).Completed == 1, TimeSpan.Zero);
        fixture.Store.Failures.ShouldBeEmpty();
    }

    [Fact]
    public async Task IgnoresUnregisteredTaskTypes()
    {
        await using var fixture = new WorkerFixture(handlerType: WorkTaskType.Summary);
        await fixture.SeedAsync();
        await fixture.SayAsync("one");

        await fixture.StartConsumerAsync();
        await Pump.UntilAsync(fixture.Clock, () => fixture.Store.Acquires.Count >= 3, Step);

        fixture.Store.Acquires.ShouldAllBe(types => types.SequenceEqual(new[] { WorkTaskType.Summary }));
        fixture.Handler!.Calls.ShouldBeEmpty();
        (await fixture.QueueAsync()).ShouldBe(new QueueCounts(1, 0, 0, 0));
    }

    [Fact]
    public async Task StopsCleanlyOnCancellation()
    {
        await using var fixture = new WorkerFixture();
        await fixture.SeedAsync();
        await fixture.SayAsync("one");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler!.Behavior = async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        };
        var consumer = await fixture.StartConsumerAsync();
        await Pump.UntilAsync(fixture.Clock, () => started.Task.IsCompleted, Step);

        await consumer.StopAsync(CancellationToken.None).WaitAsync(Wait);

        consumer.ExecuteTask.ShouldNotBeNull().IsCompletedSuccessfully.ShouldBeTrue();
        // The interrupted unit is released at once, for another worker, without counting towards dead-lettering.
        var retried = (await fixture.CompeteAsync()).ShouldNotBeNull();
        retried.Items.Single().Attempts.ShouldBe(2);
        (await fixture.QueueAsync()).DeadLettered.ShouldBe(0);
    }

    [Fact]
    public async Task WithoutHandlers_DoesNotPoll()
    {
        await using var fixture = new WorkerFixture(handlerType: null);
        var consumer = await fixture.StartConsumerAsync();

        await consumer.ExecuteTask.ShouldNotBeNull().WaitAsync(Wait);

        fixture.Store.Acquires.ShouldBeEmpty();
    }
}

/// <summary>Sums the measurements of one instrument of one <see cref="WorkerMetrics"/> instance.</summary>
internal sealed class MeasurementRecorder : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly List<double> _values = [];

    public MeasurementRecorder(WorkerMetrics metrics, string instrument)
    {
        _listener.InstrumentPublished = (published, listener) =>
        {
            if (ReferenceEquals(published.Meter.Scope, metrics) && published.Name == instrument)
            {
                listener.EnableMeasurementEvents(published);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, value, _, _) => Add(value));
        _listener.SetMeasurementEventCallback<double>((_, value, _, _) => Add(value));
        _listener.Start();
    }

    public IReadOnlyList<double> Values
    {
        get
        {
            lock (_values)
            {
                return [.. _values];
            }
        }
    }

    public double Total => Values.Sum();

    public void Dispose() => _listener.Dispose();

    private void Add(double value)
    {
        lock (_values)
        {
            _values.Add(value);
        }
    }
}
