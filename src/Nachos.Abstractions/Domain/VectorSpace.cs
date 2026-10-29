namespace Nachos.Abstractions.Domain;

/// <summary>The embedding space every store shares.</summary>
public static class VectorSpace
{
    /// <summary>
    /// The length of every stored or queried vector, fixed by the SQL schema's <c>VECTOR(1536)</c> columns
    /// (<c>text-embedding-3-small</c>). Stores reject any other length with <see cref="ArgumentException"/>.
    /// </summary>
    public const int Dimensions = 1536;
}
