using Microsoft.Extensions.AI;
using Nachos.Abstractions.Domain;

namespace Nachos.Core.Memory;

/// <summary>Embeds a query on the request path, for the reads that compare conclusions with a query text.</summary>
internal static class QueryEmbedding
{
    /// <exception cref="InvalidOperationException">
    /// No embedding generator is registered, or it returned a vector that is not <see cref="VectorSpace.Dimensions"/> long.
    /// </exception>
    public static async Task<ReadOnlyMemory<float>> EmbedAsync(
        IEmbeddingGenerator<string, Embedding<float>>? generator, string text, CancellationToken ct)
    {
        if (generator is null)
        {
            throw new InvalidOperationException(
                "A semantic query needs an embedding generator: register one with UseEmbeddingGenerator, UseAzureOpenAI or UseFakeAI.");
        }

        var vector = await generator.GenerateVectorAsync(text, cancellationToken: ct).ConfigureAwait(false);
        return vector.Length == VectorSpace.Dimensions
            ? vector
            : throw new InvalidOperationException(
                $"The embedding generator returned a {vector.Length}-dimension vector; {VectorSpace.Dimensions} are required. Check the Embeddings configuration.");
    }
}
