using Microsoft.Extensions.AI;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Core.Llm;

namespace Nachos.Core.Memory;

/// <summary>Embeds a query on the request path, for the reads that compare conclusions with a query text.</summary>
internal static class QueryEmbedding
{
    /// <exception cref="ServiceUnavailableException">
    /// No embedding generator is registered, or the embedding service failed transiently (408, 429, 5xx, a connection
    /// error or a timeout).
    /// </exception>
    /// <exception cref="InvalidOperationException">The generator returned a vector that is not <see cref="VectorSpace.Dimensions"/> long.</exception>
    public static async Task<ReadOnlyMemory<float>> EmbedAsync(
        IEmbeddingGenerator<string, Embedding<float>>? generator, string text, CancellationToken ct)
    {
        if (generator is null)
        {
            throw new ServiceUnavailableException(
                "Semantic queries are unavailable: this host has no embedding generator. Configure AzureOpenAI:Endpoint and " +
                "AzureOpenAI:EmbeddingDeployment, or register one with UseEmbeddingGenerator, UseAzureOpenAI or UseFakeAI.");
        }

        ReadOnlyMemory<float> vector;
        try
        {
            vector = await generator.GenerateVectorAsync(text, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ProviderFailure.IsTransient(ex, ct))
        {
            throw new ServiceUnavailableException("The embedding service is unavailable; retry the semantic query later.", ex);
        }

        return vector.Length == VectorSpace.Dimensions
            ? vector
            : throw new InvalidOperationException(
                $"The embedding generator returned a {vector.Length}-dimension vector; {VectorSpace.Dimensions} are required. Check the Embeddings configuration.");
    }
}
