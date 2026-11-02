using Microsoft.Extensions.AI;
using Shouldly;
using Nachos.Core.Llm;

namespace Nachos.Core.Tests.Llm;

public sealed class DeterministicEmbeddingGeneratorTests : IDisposable
{
    private readonly DeterministicEmbeddingGenerator _generator = new();

    public void Dispose() => _generator.Dispose();

    [Fact]
    public async Task Embeddings_AreUnitNormAnd1536Dimensional()
    {
        var embeddings = await _generator.GenerateAsync(["alpha", "beta", ""], cancellationToken: CancellationToken.None);

        embeddings.Count.ShouldBe(3);
        foreach (var embedding in embeddings)
        {
            embedding.Vector.Length.ShouldBe(1536);
            embedding.Vector.ToArray().Sum(x => (double)x * x).ShouldBe(1.0, 1e-4);
        }
    }

    [Fact]
    public async Task SameText_GivesSameVector_DifferentText_Differs()
    {
        var embeddings = await _generator.GenerateAsync(["alpha", "alpha", "beta"], cancellationToken: CancellationToken.None);

        embeddings[0].Vector.ToArray().ShouldBe(embeddings[1].Vector.ToArray());
        embeddings[0].Vector.ToArray().ShouldNotBe(embeddings[2].Vector.ToArray());
    }

    [Fact]
    public async Task Vector_IsPinnedAcrossProcesses()
    {
        var embedding = (await _generator.GenerateAsync(["Nachos"], cancellationToken: CancellationToken.None))[0];

        // Guards the hash scheme: changing it silently would orphan every stored fake-mode embedding.
        embedding.Vector.Span[0].ShouldBe(0.03622433f, 1e-6f);
    }

    [Fact]
    public async Task RequestedDimensions_OverrideTheDefault()
    {
        var embedding = (await _generator.GenerateAsync(
            ["alpha"], new EmbeddingGenerationOptions { Dimensions = 64 }, CancellationToken.None))[0];

        embedding.Vector.Length.ShouldBe(64);
    }
}
