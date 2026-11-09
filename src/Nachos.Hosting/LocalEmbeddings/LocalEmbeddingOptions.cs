using Nachos.Core.Llm;

namespace Nachos.Hosting.LocalEmbeddings;

/// <summary>In-process embeddings (<c>UseLocalEmbeddings</c>), bound from <c>Embeddings:Local</c>.</summary>
public sealed class LocalEmbeddingOptions
{
    /// <summary>
    /// The catalog id of the model: <c>bge-small-en-v1.5</c> (the default, MIT) or <c>all-minilm-l6-v2</c> (Apache-2.0).
    /// A database holds one model's vectors; changing it needs <c>nachos embeddings reset</c>.
    /// </summary>
    public string Model { get; set; } = LocalEmbeddingModel.Default.Id;

    /// <summary>
    /// The directory holding one folder per model (<c>{ModelDirectory}/{Model}/model.onnx</c> and <c>vocab.txt</c>), as
    /// <c>nachos models fetch</c> writes it. Null uses <c>nachos/models</c> under the user's local application data.
    /// </summary>
    public string? ModelDirectory { get; set; }

    /// <summary>The most inputs embedded in one model run; default 32.</summary>
    public int BatchSize { get; set; } = 32;
}
