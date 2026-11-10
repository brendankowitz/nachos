using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nachos.Core.Llm;
using Nachos.Core.Worker;
using Shouldly;

namespace Nachos.Worker.Tests;

/// <summary>
/// Hosted services start one after another in registration order, so <see cref="EmbeddingSpaceCheck"/> must come before the
/// loops that embed: it closes the <see cref="EmbeddingSpaceGate"/> before they can run when the store is unreachable. This
/// inspects the worker's own registration, which <c>Program.cs</c> also calls, without starting anything.
/// </summary>
public sealed class HostedServiceOrderTests
{
    [Fact]
    public void Worker_RegistersTheCheckBeforeTheWorkerLoops()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Nachos:SqlServer:ConnectionString"] = "Server=unused.example;Database=nachos;",
            ["Nachos:Ai:Mode"] = "Fake",
            ["Nachos:Ai:Embeddings"] = "Local",
        }).Build();
        var services = new ServiceCollection().AddNachosWorkerHost(configuration);

        var order = services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(descriptor => descriptor.ImplementationType)
            .ToList();
        var check = order.IndexOf(typeof(EmbeddingSpaceCheck));
        check.ShouldBeGreaterThanOrEqualTo(0);
        check.ShouldBeLessThan(order.IndexOf(typeof(ReconcilerService)));
        check.ShouldBeLessThan(order.IndexOf(typeof(QueueConsumerService)));
    }
}
