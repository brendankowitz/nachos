using Microsoft.Extensions.AI;
using Nachos.Core.Llm;

namespace Nachos.Core.Tests.Search;

/// <summary>Rejects an input over its token limit as a model would, with an error that is not transient.</summary>
internal sealed class TokenLimitedEmbeddingGenerator(int maxTokens) : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly DeterministicEmbeddingGenerator _inner = new();
    private readonly EmbeddingChunking _chunking = EmbeddingChunking.Cl100kBase();

    public List<string> Inputs { get; } = [];

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var texts = values.ToList();
        foreach (var text in texts)
        {
            Inputs.Add(text);
            if (_chunking.Tokenizer.CountTokens(text) > maxTokens)
            {
                throw new InvalidOperationException($"The input is over {maxTokens} tokens.");
            }
        }

        return _inner.GenerateAsync(texts, options, cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => _inner.GetService(serviceType, serviceKey);

    public void Dispose() => _inner.Dispose();
}
