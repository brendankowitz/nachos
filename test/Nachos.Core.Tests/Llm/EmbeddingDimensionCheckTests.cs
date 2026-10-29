using Shouldly;
using Nachos.Core.Llm;

namespace Nachos.Core.Tests.Llm;

public sealed class EmbeddingDimensionCheckTests
{
    [Fact]
    public async Task Mismatch_FailsStartupWithActionableMessage()
    {
        var check = new EmbeddingDimensionCheck(new DeterministicEmbeddingGenerator(dimensions: 768));

        var error = await Should.ThrowAsync<InvalidOperationException>(
            () => check.StartAsync(CancellationToken.None));

        error.Message.ShouldContain("768");
        error.Message.ShouldContain("1536");
        error.Message.ShouldContain("Embeddings:RequestDimensions");
    }

    [Fact]
    public async Task MatchingDimensions_StartsNormally()
    {
        var check = new EmbeddingDimensionCheck(new DeterministicEmbeddingGenerator());

        await check.StartAsync(CancellationToken.None);
    }
}
