namespace Nachos.Core.Llm;

public sealed class EmbeddingOptions
{
    /// <summary>The dimension fixed by the database schema (VECTOR(1536)).</summary>
    public const int RequiredDimensions = 1536;

    /// <summary>The <c>dimensions</c> request parameter, for models whose native size differs from <see cref="RequiredDimensions"/>.</summary>
    public int? RequestDimensions { get; set; }
}
