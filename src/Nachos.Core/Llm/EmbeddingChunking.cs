using Microsoft.ML.Tokenizers;
using Nachos.Core.Tokens;

namespace Nachos.Core.Llm;

/// <summary>
/// How the active embedding model counts tokens, registered alongside its generator: the tokenizer that sizes message
/// chunks and the largest chunk, in that tokenizer's tokens, the model accepts.
/// </summary>
/// <param name="Tokenizer">The embedding model's tokenizer.</param>
/// <param name="MaxTokens">The most tokens one embedding input may have; at least 1.</param>
public sealed record EmbeddingChunking(Tokenizer Tokenizer, int MaxTokens)
{
    /// <summary>The chunk limit of the models Nachos ships registrations for.</summary>
    public const int DefaultMaxTokens = 512;

    public Tokenizer Tokenizer { get; } = Tokenizer ?? throw new ArgumentNullException(nameof(Tokenizer));

    public int MaxTokens { get; } = MaxTokens >= 1
        ? MaxTokens
        : throw new ArgumentOutOfRangeException(nameof(MaxTokens), MaxTokens, "MaxTokens must be at least 1.");

    /// <summary>The <c>cl100k_base</c> encoding of OpenAI's text-embedding-3 and ada-002 models.</summary>
    public static EmbeddingChunking Cl100kBase(int maxTokens = DefaultMaxTokens) =>
        new(Cl100kBaseTokenizer.Instance, maxTokens);
}
