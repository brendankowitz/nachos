namespace Nachos.Core.Llm;

/// <summary>Pure vector helpers for local sentence-embedding models: pooling, L2 normalization and zero padding.</summary>
internal static class EmbeddingVectors
{
    /// <summary>
    /// Pools one sequence's hidden states (<paramref name="hidden"/>, token-major: <c>tokens × dimensions</c>) into a
    /// vector of <paramref name="dimensions"/> values. <paramref name="mask"/> has one entry per token; 0 marks padding.
    /// </summary>
    public static float[] Pool(ReadOnlySpan<float> hidden, ReadOnlySpan<long> mask, int dimensions, EmbeddingPooling pooling)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 1);
        if (mask.Length == 0 || hidden.Length != mask.Length * dimensions)
        {
            throw new ArgumentException(
                $"Expected {mask.Length} tokens of {dimensions} values, but got {hidden.Length} values.", nameof(hidden));
        }

        var pooled = new float[dimensions];
        if (pooling == EmbeddingPooling.Cls)
        {
            hidden[..dimensions].CopyTo(pooled);
            return pooled;
        }

        var kept = 0;
        for (var token = 0; token < mask.Length; token++)
        {
            if (mask[token] == 0)
            {
                continue;
            }

            kept++;
            var row = hidden.Slice(token * dimensions, dimensions);
            for (var i = 0; i < dimensions; i++)
            {
                pooled[i] += row[i];
            }
        }

        if (kept == 0)
        {
            throw new ArgumentException("The attention mask keeps no token, so there is nothing to average.", nameof(mask));
        }

        for (var i = 0; i < dimensions; i++)
        {
            pooled[i] /= kept;
        }

        return pooled;
    }

    /// <summary>Scales <paramref name="vector"/> in place to unit length.</summary>
    /// <exception cref="ArgumentException">The vector is all zeros, so it has no direction.</exception>
    public static void Normalize(Span<float> vector)
    {
        double sumOfSquares = 0;
        foreach (var value in vector)
        {
            sumOfSquares += (double)value * value;
        }

        if (sumOfSquares == 0 || !double.IsFinite(sumOfSquares))
        {
            throw new ArgumentException("A zero or non-finite vector cannot be normalized.", nameof(vector));
        }

        var norm = (float)Math.Sqrt(sumOfSquares);
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] /= norm;
        }
    }

    /// <summary>
    /// <paramref name="vector"/> followed by zeros up to <paramref name="length"/>. Padding changes no dot product or norm,
    /// so cosine distances between padded vectors are exactly those between the originals.
    /// </summary>
    public static float[] PadTo(ReadOnlySpan<float> vector, int length)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(vector.Length, length);
        var padded = new float[length];
        vector.CopyTo(padded);
        return padded;
    }
}
