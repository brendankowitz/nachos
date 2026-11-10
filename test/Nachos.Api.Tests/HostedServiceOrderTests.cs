using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nachos.Core.Llm;
using Nachos.Core.Worker;
using Nachos.Testing;
using Shouldly;

namespace Nachos.Api.Tests;

/// <summary>
/// Hosted services start one after another in registration order, so <see cref="EmbeddingSpaceCheck"/> must come before the
/// loops that embed: it closes the <see cref="EmbeddingSpaceGate"/> before they can run when the store is unreachable.
/// </summary>
public sealed class HostedServiceOrderTests
{
    private static void ShouldPrecedeTheLoops(IReadOnlyList<Type?> order)
    {
        var check = order.ToList().IndexOf(typeof(EmbeddingSpaceCheck));
        check.ShouldBeGreaterThanOrEqualTo(0);
        check.ShouldBeLessThan(order.ToList().IndexOf(typeof(ReconcilerService)));
        check.ShouldBeLessThan(order.ToList().IndexOf(typeof(QueueConsumerService)));
    }

    [Fact]
    public void Api_InMemory_RegistersTheCheckBeforeTheWorkerLoops()
    {
        using var factory = new NachosApiFactory { RunWorkerLoops = true };

        ShouldPrecedeTheLoops([.. factory.Services.GetServices<IHostedService>().Select(service => service.GetType())]);
    }

    [Fact]
    public void Worker_RegistersTheCheckBeforeTheWorkerLoops()
    {
        // The worker's Program.cs registration, inspected without starting anything.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Nachos:SqlServer:ConnectionString"] = "Server=unused.example;Database=nachos;",
            ["Nachos:Ai:Mode"] = "Fake",
            ["Nachos:Ai:Embeddings"] = "Local",
        }).Build();
        var services = new ServiceCollection();
        services.AddNachos(nachos => nachos.UseSqlServer(configuration).UseConfiguredAI(configuration).AddNachosWorker());

        ShouldPrecedeTheLoops(
            [.. services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).Select(descriptor => descriptor.ImplementationType)]);
    }
}
