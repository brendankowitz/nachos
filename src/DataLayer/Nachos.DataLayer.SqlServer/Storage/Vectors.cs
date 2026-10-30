using Nachos.Abstractions.Domain;

namespace Nachos.DataLayer.SqlServer.Storage;

/// <summary>
/// Embedding input checks, made before any round trip: the <c>VECTOR(1536)</c> columns would reject another length with a
/// SQL error (a 500); the contract makes it an <see cref="ArgumentException"/>, as in the in-memory provider.
/// </summary>
internal static class Vectors
{
    /// <exception cref="ArgumentException">The vector is not <see cref="VectorSpace.Dimensions"/> long.</exception>
    public static ReadOnlyMemory<float> Require(ReadOnlyMemory<float> vector, string paramName) =>
        vector.Length == VectorSpace.Dimensions
            ? vector
            : throw new ArgumentException(
                $"A vector must have {VectorSpace.Dimensions} dimensions; this one has {vector.Length}.", paramName);

    /// <summary><see cref="Require"/> for an optional vector; null stays null.</summary>
    /// <exception cref="ArgumentException">The vector is not <see cref="VectorSpace.Dimensions"/> long.</exception>
    public static ReadOnlyMemory<float>? RequireOptional(ReadOnlyMemory<float>? vector, string paramName)
    {
        // Not a conditional expression: its null branch would convert to an empty ReadOnlyMemory (from a null array).
        if (vector is not { } value)
        {
            return null;
        }

        return Require(value, paramName);
    }
}
