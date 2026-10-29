using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Stores;
using Nachos.Core.Llm;
using Nachos.Core.Memory;
using Shouldly;

namespace Nachos.Core.Tests.Memory;

/// <summary>The memory services resolve with or without AI and logging, and use the generator when one is registered.</summary>
public sealed class MemoryHostingTests
{
    // ValidateOnBuild needs logging for the in-memory provider's own startup notice, so a container without logging
    // is checked by resolving the memory services themselves.
    private static ServiceProvider Build(Action<IServiceCollection>? configure = null, bool withLogging = true)
    {
        var services = new ServiceCollection();
        if (withLogging)
        {
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        }

        services.AddNachos(builder => builder.UseInMemory());
        configure?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = withLogging });
    }

    [Fact]
    public async Task BareContainer_WithoutAiOrLogging_ResolvesAndLeavesCreatedConclusionsPending()
    {
        using var provider = Build(withLogging: false);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<NachosService>();
        await SeedAsync(service);

        await service.CreateConclusionsAsync("W", [new("alice likes tea", "alice", "alice")]);

        (await scope.ServiceProvider.GetRequiredService<IMemoryStore>().Embeddings.ListPendingAsync(10, default)).Count.ShouldBe(1);
        await Should.ThrowAsync<InvalidOperationException>(() => service.QueryConclusionsAsync("W", new("tea")));
        scope.ServiceProvider.GetRequiredService<PeerContextService>().ShouldNotBeNull();
    }

    [Fact]
    public async Task RegisteredGenerator_IsUsedByTheMemoryServices()
    {
        using var provider = Build(services =>
            services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new DeterministicEmbeddingGenerator()));
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<NachosService>();
        await SeedAsync(service);

        await service.CreateConclusionsAsync("W", [new("alice likes tea", "alice", "alice")]);

        (await scope.ServiceProvider.GetRequiredService<IMemoryStore>().Embeddings.ListPendingAsync(10, default)).ShouldBeEmpty();
        (await service.QueryConclusionsAsync("W", new("alice likes tea"))).ShouldHaveSingleItem();
        (await service.GetRepresentationAsync("W", "alice", new(SearchQuery: "alice likes tea")))
            .ShouldBe(new RepresentationResponse("## Explicit\n\n- alice likes tea\n"));
    }

    private static async Task SeedAsync(NachosService service)
    {
        await service.GetOrCreateWorkspaceAsync("W");
        await service.GetOrCreatePeerAsync("W", "alice");
    }
}
