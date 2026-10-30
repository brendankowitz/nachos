namespace Nachos.Core.Llm;

/// <summary>Embedding settings, bound from <c>Embeddings</c>. The generator itself comes from <c>UseAzureOpenAI</c> or <c>UseEmbeddingGenerator</c>.</summary>
public sealed class EmbeddingOptions
{
    /// <summary>The dimension fixed by the database schema (VECTOR(1536)).</summary>
    public const int RequiredDimensions = 1536;

    /// <summary>The <c>dimensions</c> request parameter, for models whose native size differs from <see cref="RequiredDimensions"/>.</summary>
    public int? RequestDimensions { get; set; }
}
