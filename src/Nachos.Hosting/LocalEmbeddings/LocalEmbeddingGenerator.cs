using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.Tokenizers;
using Nachos.Abstractions.Domain;
using Nachos.Core.Llm;

namespace Nachos.Hosting.LocalEmbeddings;

/// <summary>
/// Embeds text in-process on the CPU with a <see cref="LocalEmbeddingModel"/> through ONNX Runtime. Each vector is pooled
/// as the model was trained, L2-normalized, then zero-padded to <see cref="VectorSpace.Dimensions"/>, which leaves every
/// cosine distance exactly as the model's own. Input longer than the model's limit is truncated, not rejected.
/// </summary>
/// <remarks>
/// One <see cref="InferenceSession"/> is created and owned for the generator's lifetime; it is safe for concurrent runs.
/// Inputs run in batches of at most the configured size, and cancellation is checked between batches. The work is
/// CPU-bound and runs on the caller's thread.
/// </remarks>
public sealed class LocalEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private const string HiddenStateOutput = "last_hidden_state";

    private readonly LocalEmbeddingModel _model;
    private readonly BertTokenizer _tokenizer;
    private readonly InferenceSession _session;
    private readonly int _batchSize;
    private readonly bool _takesTokenTypes;
    private readonly EmbeddingGeneratorMetadata _metadata;

    private LocalEmbeddingGenerator(LocalEmbeddingModel model, BertTokenizer tokenizer, InferenceSession session, int batchSize)
    {
        _model = model;
        _tokenizer = tokenizer;
        _session = session;
        _batchSize = batchSize;
        _takesTokenTypes = session.InputMetadata.ContainsKey("token_type_ids");
        _metadata = new EmbeddingGeneratorMetadata(
            "nachos-onnx", providerUri: null, defaultModelId: model.Id, defaultModelDimensions: VectorSpace.Dimensions);
    }

    /// <summary>
    /// Loads <paramref name="model"/> from <c>{directory}/{model.Id}/</c> after checking every file against its pinned
    /// SHA-256, so the vectors are the ones the model id promises.
    /// </summary>
    /// <exception cref="InvalidOperationException">A file is missing or differs from the pinned one; the message says how to fetch it.</exception>
    public static LocalEmbeddingGenerator Create(LocalEmbeddingModel model, string directory, int batchSize)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        var folder = model.FolderIn(directory);
        foreach (var file in model.Files)
        {
            var path = Path.Combine(folder, file.Name);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException(
                    $"The local embedding model '{model.Id}' is missing {file.Name} in '{folder}'. " +
                    $"Run 'nachos models fetch {model.Id} --dir \"{directory}\"', or set Embeddings:Local:ModelDirectory.");
            }

            if (!file.IsAt(path))
            {
                throw new InvalidOperationException(
                    $"'{path}' is not the pinned {file.Name} of '{model.Id}' (its SHA-256 differs). Delete it and run " +
                    $"'nachos models fetch {model.Id} --dir \"{directory}\"'.");
            }
        }

        // Suppresses ONNX Runtime's optional usage events; its documentation names ORT_DISABLE_TELEMETRY=1 for the rest.
        OrtEnv.Instance().DisableTelemetryEvents();
        var tokenizer = LocalEmbeddingModel.CreateTokenizer(folder);
        using var options = new SessionOptions();
        return new LocalEmbeddingGenerator(
            model, tokenizer, new InferenceSession(Path.Combine(folder, LocalEmbeddingModel.ModelFileName), options), batchSize);
    }

    /// <summary>How message chunks are sized for this model: its tokenizer, and its limit less <c>[CLS]</c> and <c>[SEP]</c>.</summary>
    public EmbeddingChunking Chunking => new(_tokenizer, _model.MaxContentTokens);

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (options?.Dimensions is { } dimensions && dimensions != VectorSpace.Dimensions)
        {
            throw new NotSupportedException(
                $"Local embedding models produce {VectorSpace.Dimensions} dimensions ({_model.Dimensions} padded with zeros); " +
                $"unset Embeddings:RequestDimensions.");
        }

        if (options?.ModelId is { } modelId && !string.Equals(modelId, _model.Id, StringComparison.Ordinal))
        {
            throw new NotSupportedException($"This generator runs '{_model.Id}' only; '{modelId}' was requested.");
        }

        var inputs = values.ToList();
        var result = new GeneratedEmbeddings<Embedding<float>>(inputs.Count);
        for (var start = 0; start < inputs.Count; start += _batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = inputs.GetRange(start, Math.Min(_batchSize, inputs.Count - start));
            foreach (var vector in Embed(batch))
            {
                result.Add(new Embedding<float>(vector) { ModelId = _model.Id });
            }
        }

        return Task.FromResult(result);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is not null ? null
        : serviceType == typeof(EmbeddingGeneratorMetadata) ? _metadata
        : serviceType.IsInstanceOfType(this) ? this
        : null;

    public void Dispose() => _session.Dispose();

    private List<float[]> Embed(List<string> batch)
    {
        var sequences = batch.Select(Encode).ToList();
        var length = sequences.Max(sequence => sequence.Count);
        var ids = new long[batch.Count * length];
        var mask = new long[batch.Count * length];
        for (var row = 0; row < sequences.Count; row++)
        {
            for (var i = 0; i < sequences[row].Count; i++)
            {
                ids[row * length + i] = sequences[row][i];
                mask[row * length + i] = 1;
            }

            for (var i = sequences[row].Count; i < length; i++)
            {
                ids[row * length + i] = _tokenizer.PaddingTokenId;
            }
        }

        long[] shape = [batch.Count, length];
        using var idsValue = OrtValue.CreateTensorValueFromMemory(ids, shape);
        using var maskValue = OrtValue.CreateTensorValueFromMemory(mask, shape);
        using var typesValue = OrtValue.CreateTensorValueFromMemory(new long[ids.Length], shape);
        var inputs = new Dictionary<string, OrtValue> { ["input_ids"] = idsValue, ["attention_mask"] = maskValue };
        if (_takesTokenTypes)
        {
            inputs["token_type_ids"] = typesValue;
        }

        using var runOptions = new RunOptions();
        using var outputs = _session.Run(runOptions, inputs, [HiddenStateOutput]);
        var hidden = outputs[0].GetTensorDataAsSpan<float>();
        var perRow = length * _model.Dimensions;
        if (hidden.Length != batch.Count * perRow)
        {
            throw new InvalidOperationException(
                $"'{_model.Id}' returned {hidden.Length} values for {batch.Count}×{length} tokens; expected {_model.Dimensions} per token.");
        }

        var vectors = new List<float[]>(batch.Count);
        for (var row = 0; row < batch.Count; row++)
        {
            var pooled = EmbeddingVectors.Pool(
                hidden.Slice(row * perRow, perRow), mask.AsSpan(row * length, length), _model.Dimensions, _model.Pooling);
            EmbeddingVectors.Normalize(pooled);
            vectors.Add(EmbeddingVectors.PadTo(pooled, VectorSpace.Dimensions));
        }

        return vectors;
    }

    /// <summary><c>[CLS]</c>, the text's tokens truncated to the model's limit, <c>[SEP]</c>.</summary>
    private List<int> Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var content = _tokenizer.EncodeToIds(text, _model.MaxContentTokens, addSpecialTokens: false, out _, out _);
        return [_tokenizer.ClassificationTokenId, .. content, _tokenizer.SeparatorTokenId];
    }
}
