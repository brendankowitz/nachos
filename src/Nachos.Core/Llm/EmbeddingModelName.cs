using Microsoft.Extensions.AI;

namespace Nachos.Core.Llm;

/// <summary>
/// The name recorded as a conclusion's <see cref="Abstractions.Domain.VectorSpace.ModelMetadataKey"/>: the generator's
/// default model id (for Azure OpenAI, the deployment name), or <c>unknown</c> when it reports none.
/// </summary>
internal static class EmbeddingModelName
{
    public const string Unknown = "unknown";

    public static string Of(IEmbeddingGenerator<string, Embedding<float>> generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return generator.GetService<EmbeddingGeneratorMetadata>()?.DefaultModelId is { Length: > 0 } model ? model : Unknown;
    }
}
