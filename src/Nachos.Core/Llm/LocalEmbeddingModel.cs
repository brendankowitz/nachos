using Microsoft.ML.Tokenizers;

namespace Nachos.Core.Llm;

/// <summary>
/// A sentence-embedding model Nachos can run in-process (<c>UseLocalEmbeddings</c>), with its files pinned by URL and SHA-256.
/// Nothing is bundled: <c>nachos models fetch</c> downloads the files into <c>{directory}/{Id}/</c>.
/// </summary>
/// <param name="Id">The catalog id, also the model id stamped on the database and recorded with each vector.</param>
/// <param name="Licence">The SPDX id of the model's licence.</param>
/// <param name="Dimensions">The model's own vector size, before zero padding.</param>
/// <param name="MaxTokens">The longest input the model was trained on, in its tokens, including <c>[CLS]</c> and <c>[SEP]</c>.</param>
/// <param name="Pooling">How the model's token states become one vector.</param>
/// <param name="DedupMaxDistance">
/// The Deriver's dedup threshold for this model's distance scale, used when <c>Nachos:Deriver:DedupMaxDistance</c> is not
/// configured (calibrated in <c>research/local-embeddings-calibration.md</c>).
/// </param>
/// <param name="Files">The pinned files: <see cref="ModelFileName"/> and <see cref="VocabularyFileName"/>.</param>
public sealed record LocalEmbeddingModel(
    string Id,
    string Licence,
    int Dimensions,
    int MaxTokens,
    EmbeddingPooling Pooling,
    double DedupMaxDistance,
    IReadOnlyList<LocalModelFile> Files)
{
    public const string ModelFileName = "model.onnx";
    public const string VocabularyFileName = "vocab.txt";

    /// <summary><c>BAAI/bge-small-en-v1.5</c> at revision <c>5c38ec7c</c>: the default.</summary>
    public static LocalEmbeddingModel BgeSmallEnV15 { get; } = new(
        "bge-small-en-v1.5",
        "MIT",
        Dimensions: 384,
        MaxTokens: 512,
        EmbeddingPooling.Cls,
        DedupMaxDistance: 0.04,
        [
            new(ModelFileName, new("https://huggingface.co/BAAI/bge-small-en-v1.5/resolve/5c38ec7c405ec4b44b94cc5a9bb96e735b38267a/onnx/model.onnx"),
                "828e1496d7fabb79cfa4dcd84fa38625c0d3d21da474a00f08db0f559940cf35"),
            new(VocabularyFileName, new("https://huggingface.co/BAAI/bge-small-en-v1.5/resolve/5c38ec7c405ec4b44b94cc5a9bb96e735b38267a/vocab.txt"),
                "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3"),
        ]);

    /// <summary><c>sentence-transformers/all-MiniLM-L6-v2</c> at revision <c>1110a243</c>.</summary>
    public static LocalEmbeddingModel AllMiniLmL6V2 { get; } = new(
        "all-minilm-l6-v2",
        "Apache-2.0",
        Dimensions: 384,
        MaxTokens: 256,
        EmbeddingPooling.Mean,
        DedupMaxDistance: 0.035,
        [
            new(ModelFileName, new("https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/1110a243fdf4706b3f48f1d95db1a4f5529b4d41/onnx/model.onnx"),
                "6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452"),
            new(VocabularyFileName, new("https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/1110a243fdf4706b3f48f1d95db1a4f5529b4d41/vocab.txt"),
                "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3"),
        ]);

    public static LocalEmbeddingModel Default => BgeSmallEnV15;

    public static IReadOnlyList<LocalEmbeddingModel> All { get; } = [BgeSmallEnV15, AllMiniLmL6V2];

    /// <summary>
    /// Where models are kept when no directory is configured: <c>nachos/models</c> under the user's local application data
    /// (<c>%LOCALAPPDATA%</c> on Windows, <c>~/.local/share</c> on Linux).
    /// </summary>
    public static string DefaultDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "nachos", "models");

    /// <summary>The most content tokens one chunk may have: <see cref="MaxTokens"/> less <c>[CLS]</c> and <c>[SEP]</c>.</summary>
    public int MaxContentTokens => MaxTokens - 2;

    /// <summary>The catalog entry with this id (ordinal, case-insensitive), or null.</summary>
    public static LocalEmbeddingModel? Find(string? id) =>
        All.FirstOrDefault(model => string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The folder holding this model's files under <paramref name="directory"/>.</summary>
    public string FolderIn(string directory) => Path.Combine(directory, Id);

    /// <summary>
    /// The model's WordPiece tokenizer from its <c>vocab.txt</c> in <paramref name="folder"/>, configured as both catalog models were
    /// trained: lower-cased, accents stripped, CJK characters split. The file is not hash-checked here.
    /// </summary>
    public static BertTokenizer CreateTokenizer(string folder) =>
        BertTokenizer.Create(
            Path.Combine(folder, VocabularyFileName),
            new BertOptions { LowerCaseBeforeTokenization = true, RemoveNonSpacingMarks = true, IndividuallyTokenizeCjk = true });
}
