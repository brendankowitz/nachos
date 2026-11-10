using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using Nachos.Abstractions.Domain;

namespace Nachos.Core.Llm;

/// <summary>
/// Offline embedding generator for tests and fake-AI mode. Vectors are unit-norm and derived only from the input
/// text with SHA-256, so they are stable across processes and machines. Identical texts match; similar texts do not.
/// </summary>
public sealed class DeterministicEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    /// <summary>The model id this generator reports, and so the <c>embedding_model</c> of the conclusions it embeds.</summary>
    public const string ModelId = "fake";

    private const int FloatsPerHash = SHA256.HashSizeInBytes / sizeof(uint);

    private readonly int _dimensions;
    private readonly EmbeddingGeneratorMetadata _metadata;

    public DeterministicEmbeddingGenerator(int dimensions = VectorSpace.Dimensions)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 1);
        _dimensions = dimensions;
        _metadata = new EmbeddingGeneratorMetadata("nachos-deterministic", defaultModelId: ModelId, defaultModelDimensions: dimensions);
    }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        var dimensions = options?.Dimensions ?? _dimensions;
        var result = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var value in values)
        {
            result.Add(new Embedding<float>(Embed(value, dimensions)));
        }

        return Task.FromResult(result);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is not null ? null
        : serviceType == typeof(EmbeddingGeneratorMetadata) ? _metadata
        : serviceType.IsInstanceOfType(this) ? this
        : null;

    public void Dispose()
    {
    }

    private static float[] Embed(string value, int dimensions)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = Encoding.UTF8.GetBytes(value);
        var input = new byte[text.Length + sizeof(int)];
        text.CopyTo(input, sizeof(int));

        var vector = new float[dimensions];
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        double sumOfSquares = 0;
        for (var block = 0; block * FloatsPerHash < dimensions; block++)
        {
            BitConverter.TryWriteBytes(input, block);
            SHA256.HashData(input, hash);
            for (var i = 0; i < FloatsPerHash && block * FloatsPerHash + i < dimensions; i++)
            {
                var raw = BitConverter.ToUInt32(hash.Slice(i * sizeof(uint), sizeof(uint)));
                var component = (float)(raw / (double)uint.MaxValue * 2 - 1);
                vector[block * FloatsPerHash + i] = component;
                sumOfSquares += (double)component * component;
            }
        }

        var norm = (float)Math.Sqrt(sumOfSquares);
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] /= norm;
        }

        return vector;
    }
}
