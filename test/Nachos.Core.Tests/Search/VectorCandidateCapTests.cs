using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Core.Configuration;
using Nachos.Core.Llm;
using Nachos.Core.Memory;
using Nachos.Core.Search;
using NSubstitute;
using Shouldly;

namespace Nachos.Core.Tests.Search;

public sealed class VectorCandidateCapTests
{
    private static readonly MessageSearchRequest Everywhere = new("w", new MessageSearchScope.Workspace(), null);

    [Theory]
    [InlineData(1_234)]
    [InlineData(null)]
    public async Task Search_PassesTheConfiguredCapToTheStore(int? cap)
    {
        var search = Substitute.For<IMessageSearch>();
        var store = Substitute.For<IMemoryStore>();
        store.Search.Returns(search);
        var options = new NachosOptions { Search = { MaxVectorCandidates = cap } };

        await new HybridSearchService(store, Options.Create(options), new DeterministicEmbeddingGenerator())
            .SearchAsync(Everywhere, "tea", 10, VisibilityPolicy.Unrestricted, default);

        await search.Received(1).VectorAsync(
            Arg.Any<MessageSearchRequest>(), Arg.Any<ReadOnlyMemory<float>>(), HybridSearchService.CandidatesPerResult * 10, cap,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SessionAndPeerSearch_AreNotCapped()
    {
        var search = Substitute.For<IMessageSearch>();
        var store = Substitute.For<IMemoryStore>();
        store.Search.Returns(search);
        var service = new HybridSearchService(store, Options.Create(new NachosOptions()), new DeterministicEmbeddingGenerator());

        await service.SearchAsync(Everywhere with { Scope = new MessageSearchScope.Session("s") }, "tea", 10, VisibilityPolicy.Unrestricted, default);
        await service.SearchAsync(Everywhere with { Scope = new MessageSearchScope.PeerPerspective("p") }, "tea", 10, VisibilityPolicy.Unrestricted, default);

        await search.Received(2).VectorAsync(
            Arg.Any<MessageSearchRequest>(), Arg.Any<ReadOnlyMemory<float>>(), Arg.Any<int>(), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Default_IsTheBenchmarkedCap() =>
        new NachosOptions().Search.MaxVectorCandidates.ShouldBe(SearchOptions.DefaultMaxVectorCandidates);

    [Fact]
    public void EmptyConfigurationValue_TurnsTheCapOff()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Nachos:Search:MaxVectorCandidates"] = "" })
            .Build();
        var options = new NachosOptions();

        configuration.GetSection("Nachos").Bind(options);

        options.Search.MaxVectorCandidates.ShouldBeNull();
    }
}
