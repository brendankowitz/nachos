using Microsoft.Extensions.AI;

namespace Nachos.Core.Llm;

/// <summary>Waits for the <see cref="EmbeddingSpaceGate"/> before every embedding request.</summary>
internal sealed class GatedEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> inner, EmbeddingSpaceGate gate)
    : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
{
    /// <summary>The generator behind the gate, for the startup probe, which must never wait on the gate it decides about.</summary>
    public IEmbeddingGenerator<string, Embedding<float>> Ungated => InnerGenerator;

    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await base.GenerateAsync(values, options, cancellationToken).ConfigureAwait(false);
    }
}
