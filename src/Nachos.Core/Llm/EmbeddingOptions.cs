namespace Nachos.Core.Llm;

/// <summary>Embedding settings, bound from <c>Embeddings</c>. The generator itself comes from <c>UseAzureOpenAI</c> or <c>UseEmbeddingGenerator</c>.</summary>
public sealed class EmbeddingOptions
{
    /// <summary>
    /// The <c>dimensions</c> request parameter, for models whose native size differs from the
    /// <see cref="Abstractions.Domain.VectorSpace.Dimensions"/> (1536) the database stores.
    /// </summary>
    public int? RequestDimensions { get; set; }
}
