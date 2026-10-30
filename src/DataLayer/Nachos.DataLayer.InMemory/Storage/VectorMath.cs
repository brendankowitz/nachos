using Nachos.Abstractions.Domain;

namespace Nachos.DataLayer.InMemory.Storage;

/// <summary>Brute-force exact cosine distance, the same metric as the SQL provider's <c>VECTOR_DISTANCE('cosine', …)</c>.</summary>
internal static class VectorMath
{
    /// <summary>A store-owned copy of an input vector.</summary>
    /// <exception cref="ArgumentException">
    /// The vector is not <see cref="VectorSpace.Dimensions"/> long, or has a NaN or infinite component (which the SQL
    /// provider's <c>VECTOR</c> column cannot store).
    /// </exception>
    public static float[] Own(ReadOnlyMemory<float> vector, string paramName)
    {
        if (vector.Length != VectorSpace.Dimensions)
        {
            throw new ArgumentException(
                $"A vector must have {VectorSpace.Dimensions} dimensions; this one has {vector.Length}.", paramName);
        }

        var owned = vector.ToArray();
        return owned.All(float.IsFinite)
            ? owned
            : throw new ArgumentException("A vector's components must all be finite numbers.", paramName);
    }

    /// <summary>A store-owned copy of an optional input vector; null stays null.</summary>
    /// <exception cref="ArgumentException">The vector is not <see cref="VectorSpace.Dimensions"/> long.</exception>
    public static float[]? OwnOptional(ReadOnlyMemory<float>? vector, string paramName) =>
        vector is { } value ? Own(value, paramName) : null;

    /// <summary><c>1 - cos(a, b)</c>, from 0 (same direction) to 2 (opposite); 1 when either vector is zero.</summary>
    public static double CosineDistance(float[] a, float[] b)
    {
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            normA += (double)a[i] * a[i];
            normB += (double)b[i] * b[i];
        }

        return normA == 0 || normB == 0 ? 1 : 1 - (dot / (Math.Sqrt(normA) * Math.Sqrt(normB)));
    }
}
