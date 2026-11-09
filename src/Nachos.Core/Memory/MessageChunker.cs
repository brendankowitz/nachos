using Microsoft.ML.Tokenizers;
using Nachos.Core.Llm;

namespace Nachos.Core.Memory;

/// <summary>
/// Splits message content into the pieces that are embedded for search (spec §11.1): at most
/// <c>min(Embeddings:ChunkTokens, EmbeddingChunking.MaxTokens)</c> tokens of the embedding model's tokenizer each, with
/// 20% of that size shared between neighbours.
/// </summary>
public sealed class MessageChunker
{
    private const int OverlapPercent = 20;

    private readonly Tokenizer _tokenizer;
    private readonly int _chunkTokens;
    private readonly int _stepTokens;

    /// <param name="chunking">The active embedding model's tokenizer and limit.</param>
    /// <param name="chunkTokens">The configured <c>Embeddings:ChunkTokens</c>; the model's limit caps it.</param>
    public MessageChunker(EmbeddingChunking chunking, int chunkTokens)
    {
        ArgumentNullException.ThrowIfNull(chunking);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkTokens, 1);
        _tokenizer = chunking.Tokenizer;
        _chunkTokens = Math.Min(chunkTokens, chunking.MaxTokens);
        _stepTokens = _chunkTokens - _chunkTokens * OverlapPercent / 100;
    }

    /// <summary>
    /// The chunks of <paramref name="content"/>, in order: none for blank content, one when it fits a chunk. Every chunk
    /// re-tokenizes to at most the chunk size, even where a cut changes how the tokenizer merges the text around it.
    /// </summary>
    public IReadOnlyList<string> Chunk(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        if (_tokenizer.CountTokens(content) <= _chunkTokens)
        {
            return [content];
        }

        var tokens = _tokenizer.EncodeToTokens(content, out var normalized);
        var text = normalized ?? content;
        var chunks = new List<string>();
        var first = 0;
        while (true)
        {
            var last = Math.Min(first + _chunkTokens, tokens.Count);
            var chunk = Fit(text, tokens, first, ref last);
            if (!string.IsNullOrWhiteSpace(chunk))
            {
                chunks.Add(chunk);
            }

            if (last >= tokens.Count)
            {
                return chunks;
            }

            first = Math.Max(first + 1, Math.Min(first + _stepTokens, last - 1));
        }
    }

    /// <summary>The text of tokens <c>[first, last)</c>, with <paramref name="last"/> lowered until it re-tokenizes within the limit.</summary>
    private string Fit(string text, IReadOnlyList<EncodedToken> tokens, int first, ref int last)
    {
        var start = tokens[first].Offset.Start.Value;
        while (true)
        {
            var chunk = Slice(text, start, tokens[last - 1].Offset.End.Value);
            if (last - first == 1 || _tokenizer.CountTokens(chunk) <= _chunkTokens)
            {
                return chunk;
            }

            last--;
        }
    }

    // A cut inside a surrogate pair would leave a lone surrogate, which does not survive a round trip to the database.
    private static string Slice(string text, int start, int end)
    {
        if (start > 0 && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1]))
        {
            start--;
        }

        if (end < text.Length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end]))
        {
            end++;
        }

        return text[start..end];
    }
}
