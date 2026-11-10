namespace Nachos.Abstractions.Domain;

/// <summary>The embedding space every store shares.</summary>
public static class VectorSpace
{
    /// <summary>
    /// The length of every stored or queried vector, fixed by the SQL schema's <c>VECTOR(1536)</c> columns
    /// (<c>text-embedding-3-small</c>; local models' smaller vectors are zero-padded to it). Stores reject any other length
    /// with <see cref="ArgumentException"/>.
    /// </summary>
    public const int Dimensions = 1536;

    /// <summary>
    /// The <see cref="ConclusionRecord.InternalMetadata"/> key naming the model that produced a conclusion's vector (an
    /// Azure OpenAI deployment name, a local model id, or <c>fake</c>). The database itself holds one model's vectors
    /// (<see cref="EmbeddingSpace"/>); before it is stamped, these names decide whether it can be.
    /// </summary>
    public const string ModelMetadataKey = "embedding_model";
}
