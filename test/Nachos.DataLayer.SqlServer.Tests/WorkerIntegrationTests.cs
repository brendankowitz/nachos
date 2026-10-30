using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Core.Configuration;
using Nachos.Core.Worker;
using Nachos.Testing;
using Shouldly;

namespace Nachos.DataLayer.SqlServer.Tests;

/// <summary>
/// Messages become conclusions end to end on SQL Server: the real Deriver (fake AI) run by two queue consumers that share
/// one database, each unit derived exactly once.
/// </summary>
[Collection(M2StoreContracts.Name)]
public sealed class WorkerIntegrationTests(SqlServerFixture fixture)
{
    private static readonly string[] Peers = ["alice", "bob"];
    private static readonly string[] Sessions = ["S1", "S2", "S3"];
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task EndToEnd_AppendToConclusions_TwoWorkers()
    {
        var database = await SqlTestDatabase.GetAsync(fixture, "m2-worker-sql");
        await database.ResetQueueAsync();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero));
        var store = database.CreateStore(clock);
        await using var provider = BuildServices(store, clock);
        var workspace = $"ws-{Guid.NewGuid():N}";
        await store.Workspaces.GetOrCreateAsync(workspace, null, null, Ct);
        var said = new List<(string Sender, string Content)>();
        foreach (var session in Sessions)
        {
            await store.Sessions.GetOrCreateAsync(
                workspace,
                session,
                null,
                null,
                Peers.ToDictionary(peer => peer, _ => new SessionPeerConfig(ObserveOthers: true)),
                Ct);
            foreach (var sender in Peers)
            {
                for (var i = 0; i < 4; i++)
                {
                    var content = $"{sender} said {i} in {session}";
                    await store.Messages.AppendAsync(
                        workspace, session, [new NewMessage(sender, content, 5, null, null)], null, [DeriveIntent.Derive], Ct);
                    said.Add((sender, content));
                }
            }
        }

        var consumers = new[]
        {
            ActivatorUtilities.CreateInstance<QueueConsumerService>(provider),
            ActivatorUtilities.CreateInstance<QueueConsumerService>(provider),
        };
        foreach (var consumer in consumers)
        {
            await consumer.StartAsync(Ct);
        }

        try
        {
            var watch = Stopwatch.StartNew();
            while ((await TotalsAsync(store, workspace)).Completed < said.Count)
            {
                watch.Elapsed.ShouldBeLessThan(Timeout, "the queue was not drained");
                clock.Advance(TimeSpan.FromSeconds(1));
                // A real yield, so the consumers' continuations run; the outcome never depends on how long it takes.
                await Task.Delay(10);
            }
        }
        finally
        {
            foreach (var consumer in consumers)
            {
                await consumer.StopAsync(Ct);
                consumer.Dispose();
            }
        }

        (await TotalsAsync(store, workspace)).ShouldBe(new QueueCounts(0, 0, said.Count, 0));
        var conclusions = (await store.Conclusions.ListAsync(
            workspace, null, ConclusionOrder.CreatedAt, new PageRequest(1, PageRequest.MaxSize), Ct)).Items;
        // Both peers observe each other, so every message is one conclusion in each observer's collection about its sender.
        var expected = said
            .SelectMany(m => Peers.Select(observer => (observer, m.Sender, m.Content)))
            .Order()
            .ToList();
        conclusions.Select(c => (c.ObserverName, c.ObservedName, c.Content)).Order().ShouldBe(expected);
        conclusions.ShouldAllBe(c => c.TimesDerived == 1 && c.Level == ConclusionLevel.Explicit);
    }

    private static ServiceProvider BuildServices(SqlMemoryStore store, TimeProvider clock)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(clock);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection([]).Build());
        services.AddNachos(builder => builder.UseFakeAI().AddNachosWorker());
        services.Replace(ServiceDescriptor.Singleton<IMemoryStore>(store));
        services.Configure<NachosOptions>(options =>
        {
            options.Deriver.EligibilityTokens = 0;
            options.Deriver.MaxAge = TimeSpan.Zero;
            options.Deriver.BatchTokenTarget = 10;
        });
        services.Configure<WorkerOptions>(options =>
        {
            options.MaxConcurrency = 2;
            options.IdleDelay = TimeSpan.FromSeconds(1);
            options.MaxIdleDelay = TimeSpan.FromSeconds(1);
        });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static async Task<QueueCounts> TotalsAsync(SqlMemoryStore store, string workspace) =>
        (await store.WorkQueue.GetStatusAsync(workspace, null, null, null, Ct)).Totals;
}
