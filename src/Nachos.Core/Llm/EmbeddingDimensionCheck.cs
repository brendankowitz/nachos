using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;

namespace Nachos.Core.Llm;

/// <summary>Embeds one probe string at startup and fails the host if the vector size is not the one the schema stores.</summary>
public sealed class EmbeddingDimensionCheck(IEmbeddingGenerator<string, Embedding<float>> generator) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var embeddings = await generator.GenerateAsync(["nachos embedding dimension probe"], cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var actual = embeddings[0].Vector.Length;
        if (actual != EmbeddingOptions.RequiredDimensions)
        {
            throw new InvalidOperationException(
                $"The configured embedding model returned {actual} dimensions but the database stores {EmbeddingOptions.RequiredDimensions}. " +
                $"Use a {EmbeddingOptions.RequiredDimensions}-dimension model such as text-embedding-3-small, " +
                $"or set Embeddings:RequestDimensions to {EmbeddingOptions.RequiredDimensions} if the model supports the dimensions parameter.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
