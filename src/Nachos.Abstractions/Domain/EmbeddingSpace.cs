namespace Nachos.Abstractions.Domain;

/// <summary>
/// The one embedding model whose vectors a database holds (its stamp). Vectors from different models are never comparable,
/// so a database records exactly one and every host checks it at startup.
/// </summary>
/// <param name="Model">The model id, as the generator reports it (for Azure OpenAI, the deployment name); 1–<see cref="MaxModelLength"/> characters.</param>
/// <param name="Dimensions">The model's own vector size, before any zero padding to <see cref="VectorSpace.Dimensions"/>.</param>
public sealed record EmbeddingSpace(string Model, int Dimensions)
{
    /// <summary>The longest model id a store accepts (the SQL <c>Model nvarchar(128)</c> column).</summary>
    public const int MaxModelLength = 128;

    public string Model { get; } = !string.IsNullOrWhiteSpace(Model) && Model.Length <= MaxModelLength
        ? Model
        : throw new ArgumentException($"An embedding model id must be 1-{MaxModelLength} characters and not blank.", nameof(Model));

    public int Dimensions { get; } = Dimensions is >= 1 and <= VectorSpace.Dimensions
        ? Dimensions
        : throw new ArgumentOutOfRangeException(nameof(Dimensions), Dimensions, $"Dimensions must be between 1 and {VectorSpace.Dimensions}.");
}
