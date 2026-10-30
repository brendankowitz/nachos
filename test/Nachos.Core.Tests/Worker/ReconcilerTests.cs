using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Core.Llm;
using Nachos.Core.Worker;
using Shouldly;

namespace Nachos.Core.Tests.Worker;

public sealed class ReconcilerTests
{
    private const string Workspace = WorkerFixture.WorkspaceId;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task EmbedsPending()
    {
        await using var fixture = new WorkerFixture(handlerType: null);
        await fixture.SeedAsync();
        var generator = new SelectiveEmbeddings();
        var created = await InsertAsync(fixture.Store, "alice likes tea", "alice owns a cat");
        (await fixture.Store.Embeddings.ListPendingAsync(10, Ct)).Count.ShouldBe(2);

        await using var reconciler = await StartAsync(fixture, fixture.Store, generator);
        await Pump.UntilAsync(fixture.Clock, () => fixture.Store.Ticks >= 1, Interval);

        (await fixture.Store.Embeddings.ListPendingAsync(10, Ct)).ShouldBeEmpty();
        var match = (await QueryAsync(fixture.Store, "alice likes tea"))[0];
        match.Conclusion.PublicId.ShouldBe(created[0].PublicId);
        match.Distance.ShouldBe(0, 1e-6);
    }

    [Fact]
    public async Task EmbeddingFailure_RetriesThenMarksFailed()
    {
        await using var fixture = new WorkerFixture(handlerType: null);
        await fixture.SeedAsync();
        var generator = new SelectiveEmbeddings { Rejected = { "poison" } };
        await InsertAsync(fixture.Store, "alice likes tea", "poison");

        await using var reconciler = await StartAsync(fixture, fixture.Store, generator, options => options.EmbeddingMaxAttempts = 3);
        await Pump.UntilAsync(fixture.Clock, async () => (await fixture.Store.Embeddings.ListPendingAsync(10, Ct)).Count == 0, Interval);

        // Tick 1: the batch fails, then each conclusion alone (the good one is saved); ticks 2 and 3 retry the poison alone.
        // The service rejects the poison (400), so every failure counts even when nothing else succeeds.
        generator.Calls.ShouldBe([
            ["alice likes tea", "poison"],
            ["alice likes tea"],
            ["poison"],
            ["poison"],
            ["poison"],
        ]);
        (await QueryAsync(fixture.Store, "alice likes tea")).Select(m => m.Conclusion.Content).ShouldBe(["alice likes tea"]);
    }

    [Fact]
    public async Task AllFail_AdvancesNoCounts()
    {
        await using var fixture = new WorkerFixture(handlerType: null);
        await fixture.SeedAsync();
        var generator = new SelectiveEmbeddings { IsDown = true };
        await InsertAsync(fixture.Store, "alice likes tea", "alice owns a cat");

        await using var reconciler = await StartAsync(fixture, fixture.Store, generator, options => options.EmbeddingMaxAttempts = 2);
        await Pump.UntilAsync(fixture.Clock, () => fixture.Store.Ticks >= 6, Interval);

        // An outage: nothing succeeded in any tick, so nothing was marked failed and the backlog is intact.
        (await fixture.Store.Embeddings.ListPendingAsync(10, Ct)).Count.ShouldBe(2);
        generator.IsDown = false;
        await Pump.UntilAsync(fixture.Clock, async () => (await fixture.Store.Embeddings.ListPendingAsync(10, Ct)).Count == 0, Interval);
        (await QueryAsync(fixture.Store, "alice owns a cat")).Count.ShouldBe(2);
    }

    [Fact]
    public async Task UnavailableFailure_CountsOnlyInTicksWhereAnotherEmbeddingSucceeded()
    {
        await using var fixture = new WorkerFixture(handlerType: null);
        await fixture.SeedAsync();
        var generator = new SelectiveEmbeddings { Unavailable = { "flaky" } };
        await InsertAsync(fixture.Store, "alice likes tea", "flaky");

        await using var reconciler = await StartAsync(fixture, fixture.Store, generator, options => options.EmbeddingMaxAttempts = 2);
        // Tick 1 counts (tea succeeded); the ticks after, with flaky alone, do not.
        await Pump.UntilAsync(fixture.Clock, () => fixture.Store.Ticks >= 6, Interval);
        (await fixture.Store.Embeddings.ListPendingAsync(10, Ct)).Single().Content.ShouldBe("flaky");

        // With another conclusion embedded in the same tick, the second failure counts and marks it failed.
        await InsertAsync(fixture.Store, "alice owns a cat");
        await Pump.UntilAsync(fixture.Clock, async () => (await fixture.Store.Embeddings.ListPendingAsync(10, Ct)).Count == 0, Interval);
        (await QueryAsync(fixture.Store, "flaky")).ShouldNotContain(m => m.Conclusion.Content == "flaky");
    }

    [Fact]
    public async Task PurgesOldTombstonesOnly()
    {
        await using var fixture = new WorkerFixture(handlerType: null);
        await fixture.SeedAsync();
        var created = await InsertAsync(fixture.Store, "old", "young", "live");
        await fixture.Store.Conclusions.TombstoneAsync(Workspace, created[0].PublicId, Ct);
        fixture.Clock.Advance(TimeSpan.FromHours(23));
        await fixture.Store.Conclusions.TombstoneAsync(Workspace, created[1].PublicId, Ct);
        fixture.Clock.Advance(TimeSpan.FromHours(2));

        await using var reconciler = await StartAsync(fixture, fixture.Store, generator: null);
        await Pump.UntilAsync(fixture.Clock, () => fixture.Store.Ticks >= 1, TimeSpan.Zero);

        // Only the young tombstone is left to purge; the live conclusion is untouched.
        (await fixture.Inner.Conclusions.PurgeTombstonesAsync(TimeSpan.Zero, Ct)).ShouldBe(1);
        (await fixture.Store.Conclusions.GetAsync(Workspace, created[2].PublicId, Ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task PrunesOldCompletedWorkAndExpiredIdempotencyRecords()
    {
        await using var fixture = new WorkerFixture(handlerType: null);
        await fixture.SeedAsync();
        await fixture.Store.Messages.AppendAsync(
            Workspace,
            WorkerFixture.SessionId,
            [new NewMessage("alice", "one", 5, null, null)],
            new IdempotencyWrite("key", new string('a', 64), 201, _ => "{}", TimeSpan.FromMinutes(10)),
            [DeriveIntent.Derive],
            Ct);
        await fixture.Store.WorkQueue.CompleteAsync((await fixture.CompeteAsync()).ShouldNotBeNull(), Ct);
        fixture.Clock.Advance(TimeSpan.FromDays(7));

        await using var reconciler = await StartAsync(fixture, fixture.Store, generator: null);
        await Pump.UntilAsync(fixture.Clock, () => fixture.Store.Ticks >= 1, TimeSpan.Zero);

        (await fixture.QueueAsync()).Total.ShouldBe(0);
        (await fixture.Inner.Idempotency.PurgeExpiredAsync(Ct)).ShouldBe(0);
        (await fixture.Store.Embeddings.ListPendingAsync(10, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task WithoutGenerator_LeavesPendingAndStillPurges()
    {
        await using var fixture = new WorkerFixture(handlerType: null);
        await fixture.SeedAsync();
        var created = await InsertAsync(fixture.Store, "pending", "deleted");
        await fixture.Store.Conclusions.TombstoneAsync(Workspace, created[1].PublicId, Ct);

        await using var reconciler = await StartAsync(
            fixture, fixture.Store, generator: null, options => options.TombstoneRetention = TimeSpan.Zero);
        await Pump.UntilAsync(fixture.Clock, () => fixture.Store.Ticks >= 1, TimeSpan.Zero);

        (await fixture.Store.Embeddings.ListPendingAsync(10, Ct)).Single().ConclusionId.ShouldBe(created[0].PublicId);
        (await fixture.Inner.Conclusions.PurgeTombstonesAsync(TimeSpan.Zero, Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task SingleReplicaUnderLease()
    {
        await using var fixture = new WorkerFixture(handlerType: null);
        await fixture.SeedAsync();
        var first = new ObservedStore(fixture.Inner);
        var second = new ObservedStore(fixture.Inner);

        var leader = await StartAsync(fixture, first, generator: null);
        await Pump.UntilAsync(fixture.Clock, () => first.Ticks >= 1, TimeSpan.Zero);
        await using var follower = await StartAsync(fixture, second, generator: null);

        // Well past several lease durations: the leader keeps renewing, so the follower never runs a tick.
        // Small steps, so a slow continuation cannot let the clock run past a lease before its renewal.
        await Pump.UntilAsync(fixture.Clock, () => first.Ticks >= 12, Interval / 6);
        second.Ticks.ShouldBe(0);

        // Stopping releases the lease, and the follower takes over at its next attempt.
        await leader.DisposeAsync();
        var ticks = first.Ticks;
        var stoppedAt = fixture.Clock.GetUtcNow();
        await Pump.UntilAsync(fixture.Clock, () => second.Ticks >= 1, Interval / 6);
        (fixture.Clock.GetUtcNow() - stoppedAt).ShouldBeLessThan(new ReconcilerOptions().LeaseDuration, "taken over before the lease could expire");
        first.Ticks.ShouldBe(ticks);
    }

    [Fact]
    public async Task BacklogGauges_ReportTheLastTick_AndClearWhenATickFails()
    {
        await using var fixture = new WorkerFixture();
        await fixture.SeedAsync();
        await fixture.SayAsync("one");
        await InsertAsync(fixture.Store, "pending conclusion");

        await using var reconciler = await StartAsync(fixture, fixture.Store, generator: null);
        await Pump.UntilAsync(fixture.Clock, () => fixture.Store.Ticks >= 1, Interval);
        Gauge(fixture.Metrics, "nachos.queue.eligible_units").ShouldBe([1]);
        Gauge(fixture.Metrics, "nachos.conclusions.embedding_pending").ShouldBe([1]);
        Gauge(fixture.Metrics, "nachos.queue.oldest_age").ShouldHaveSingleItem().ShouldBeGreaterThanOrEqualTo(0);

        fixture.Store.FailBacklog = true;
        await Pump.UntilAsync(fixture.Clock, () => Gauge(fixture.Metrics, "nachos.queue.eligible_units").Count == 0, Interval);
        Gauge(fixture.Metrics, "nachos.conclusions.embedding_pending").ShouldBeEmpty();
        Gauge(fixture.Metrics, "nachos.queue.oldest_age").ShouldBeEmpty();

        fixture.Store.FailBacklog = false;
        await Pump.UntilAsync(fixture.Clock, () => Gauge(fixture.Metrics, "nachos.queue.eligible_units").Count == 1, Interval);
    }

    /// <summary>The values an observable gauge of <paramref name="metrics"/> reports right now.</summary>
    private static List<double> Gauge(WorkerMetrics metrics, string instrument)
    {
        var values = new List<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (published, l) =>
        {
            if (ReferenceEquals(published.Meter.Scope, metrics) && published.Name == instrument)
            {
                l.EnableMeasurementEvents(published);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => values.Add(value));
        listener.SetMeasurementEventCallback<double>((_, value, _, _) => values.Add(value));
        listener.Start();
        listener.RecordObservableInstruments();
        return values;
    }

    private static async Task<Running> StartAsync(
        WorkerFixture fixture,
        IMemoryStore store,
        IEmbeddingGenerator<string, Embedding<float>>? generator,
        Action<ReconcilerOptions>? configure = null)
    {
        var options = new ReconcilerOptions { Interval = Interval };
        configure?.Invoke(options);
        var service = new ReconcilerService(
            store,
            fixture.Clock,
            Options.Create(options),
            fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            fixture.Metrics,
            NullLogger<ReconcilerService>.Instance,
            generator);
        await service.StartAsync(Ct);
        return new Running(service);
    }

    private static Task<IReadOnlyList<ConclusionRecord>> InsertAsync(ObservedStore store, params string[] contents) =>
        store.Conclusions.InsertAsync(
            Workspace,
            [.. contents.Select(content => new NewConclusion("alice", "alice", null, ConclusionLevel.Explicit, content, null, [], null))],
            Ct);

    private static async Task<IReadOnlyList<ConclusionMatch>> QueryAsync(ObservedStore store, string text)
    {
        using var generator = new DeterministicEmbeddingGenerator();
        var vector = (await generator.GenerateAsync([text], cancellationToken: Ct))[0].Vector;
        return await store.Conclusions.QueryAsync(Workspace, vector, 10, null, null, Ct);
    }

    private sealed class Running(ReconcilerService service) : IAsyncDisposable
    {
        private bool _disposed;

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await service.StopAsync(Ct);
            service.ExecuteTask.ShouldNotBeNull().IsCompletedSuccessfully.ShouldBeTrue();
            service.Dispose();
        }
    }

    /// <summary>
    /// Deterministic embeddings that fail any call containing a <see cref="Rejected"/> text with HTTP 400, or an
    /// <see cref="Unavailable"/> text with HTTP 503, and every call with 503 while <see cref="IsDown"/>.
    /// </summary>
    private sealed class SelectiveEmbeddings : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly DeterministicEmbeddingGenerator _inner = new();

        public HashSet<string> Rejected { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Unavailable { get; } = new(StringComparer.Ordinal);

        public volatile bool IsDown;

        public ConcurrentQueue<string[]> Calls { get; } = new();

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            var texts = values.ToArray();
            Calls.Enqueue(texts);
            if (texts.Any(Rejected.Contains))
            {
                throw new HttpRequestException("input rejected", null, HttpStatusCode.BadRequest);
            }

            return IsDown || texts.Any(Unavailable.Contains)
                ? throw new HttpRequestException("service unavailable", null, HttpStatusCode.ServiceUnavailable)
                : _inner.GenerateAsync(texts, options, cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => _inner.GetService(serviceType, serviceKey);

        public void Dispose() => _inner.Dispose();
    }
}
