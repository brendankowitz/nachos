using Microsoft.Extensions.DependencyInjection;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Shouldly;

namespace Nachos.Core.Tests.Worker;

/// <summary>Messages become conclusions through the real Deriver (fake AI), with two consumers sharing one store.</summary>
public sealed class WorkerEndToEndTests
{
    private static readonly string[] Peers = ["alice", "bob"];

    [Fact]
    public async Task EndToEnd_AppendToConclusions_TwoWorkers()
    {
        await using var fixture = new WorkerFixture(
            handlerType: null,
            configure: builder => builder.UseFakeAI().AddNachosWorker(),
            configureNachos: options =>
            {
                options.Deriver.EligibilityTokens = 0;
                options.Deriver.MaxAge = TimeSpan.Zero;
                options.Deriver.BatchTokenTarget = 10;
            },
            configureWorker: options => options.MaxConcurrency = 2);
        string[] sessions = ["S1", "S2", "S3"];
        await fixture.SeedAsync(sessions);
        var said = new List<(string Sender, string Content)>();
        foreach (var session in sessions)
        {
            foreach (var sender in Peers)
            {
                for (var i = 0; i < 4; i++)
                {
                    var content = $"{sender} said {i} in {session}";
                    await fixture.SayAsync(content, sender, session);
                    said.Add((sender, content));
                }
            }
        }

        using var facts = new MeasurementRecorder(fixture.Metrics, "nachos.deriver.facts");
        await fixture.StartConsumerAsync();
        await fixture.StartConsumerAsync();
        await Pump.UntilAsync(fixture.Clock, async () => (await fixture.QueueAsync()).Completed == said.Count, TimeSpan.FromSeconds(1));

        (await fixture.QueueAsync()).ShouldBe(new QueueCounts(0, 0, said.Count, 0));
        facts.Total.ShouldBe(said.Count);
        var conclusions = (await fixture.Store.Conclusions.ListAsync(
            WorkerFixture.WorkspaceId, null, ConclusionOrder.CreatedAt, new PageRequest(1, PageRequest.MaxSize), CancellationToken.None)).Items;
        // Both peers observe each other, so every message is one conclusion in each observer's collection about its sender.
        var expected = said
            .SelectMany(m => Peers.Select(observer => (observer, m.Sender, m.Content)))
            .Order()
            .ToList();
        conclusions.Select(c => (c.ObserverName, c.ObservedName, c.Content)).Order().ShouldBe(expected);
        conclusions.ShouldAllBe(c => c.TimesDerived == 1 && c.Level == ConclusionLevel.Explicit);
        fixture.Store.Failures.ShouldBeEmpty();
        fixture.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().ShouldNotBeEmpty();
    }
}
