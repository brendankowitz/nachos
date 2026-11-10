using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nachos.Core.Llm;
using Nachos.Core.Worker;
using Nachos.Testing;
using Shouldly;

namespace Nachos.Api.Tests;

/// <summary>
/// Hosted services start one after another in registration order, so <see cref="EmbeddingSpaceCheck"/> must come before the
/// loops that embed: it closes the <see cref="EmbeddingSpaceGate"/> before they can run when the store is unreachable. The
/// worker's registration is pinned in Nachos.Worker.Tests.
/// </summary>
public sealed class HostedServiceOrderTests
{
    [Fact]
    public void Api_InMemory_RegistersTheCheckBeforeTheWorkerLoops()
    {
        using var factory = new NachosApiFactory { RunWorkerLoops = true };

        var order = factory.Services.GetServices<IHostedService>().Select(service => service.GetType()).ToList();

        var check = order.IndexOf(typeof(EmbeddingSpaceCheck));
        check.ShouldBeGreaterThanOrEqualTo(0);
        check.ShouldBeLessThan(order.IndexOf(typeof(ReconcilerService)));
        check.ShouldBeLessThan(order.IndexOf(typeof(QueueConsumerService)));
    }
}
