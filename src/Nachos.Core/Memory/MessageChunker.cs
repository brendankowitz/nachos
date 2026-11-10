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
    /// The chunks of <paramref name="content"/>, in order: none for blank content, one when it fits a chunk. Every chunk is
    /// a piece of <paramref name="content"/> itself, never the tokenizer's normalized form, and re-tokenizes to at most the
    /// chunk size, even where a cut changes how the tokenizer merges the text around it.
    /// </summary>
    public IReadOnlyList<string> Chunk(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        // One pass counts every token and reports the normalized text (2.0.0 has no CountTokens overload that returns it).
        _tokenizer.GetIndexByTokenCount(content, int.MaxValue, out var normalized, out var tokenCount);
        if (tokenCount <= _chunkTokens)
        {
            return [content];
        }

        // A normalizing tokenizer (BERT lower-cases, strips accents, spaces out CJK) reports offsets into its normalized
        // text, whose length can differ from the original, so its offsets cannot locate a cut in the original.
        return normalized is null || string.Equals(normalized, content, StringComparison.Ordinal)
            ? ChunkByTokenOffsets(content, _tokenizer.EncodeToTokens(content, out _))
            : ChunkByMeasuring(content);
    }

    private List<string> ChunkByTokenOffsets(string text, IReadOnlyList<EncodedToken> tokens)
    {
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

    /// <summary>
    /// Cuts the original text by measuring it: each chunk is the longest piece from its start that counts within the
    /// limit, moved back to the last whitespace in its second half, and the next one starts at the latest word start that
    /// keeps about 20% of the chunk size as overlap. It needs no offsets: it counts candidate pieces, a logarithmic number
    /// of them per chunk.
    /// </summary>
    private List<string> ChunkByMeasuring(string content)
    {
        var overlapTokens = _chunkTokens - _stepTokens;
        var chunks = new List<string>();
        var start = SkipWhiteSpace(content, 0);
        while (start < content.Length)
        {
            var end = LongestFit(content, start);
            if (end < content.Length)
            {
                end = BackToWhiteSpace(content, start, end);
            }

            var chunk = content[start..end];
            if (!string.IsNullOrWhiteSpace(chunk))
            {
                chunks.Add(chunk);
            }

            if (end >= content.Length)
            {
                break;
            }

            start = SkipWhiteSpace(content, Math.Max(OverlapStart(content, start, end, overlapTokens), NextBoundary(content, start)));
        }

        return chunks;
    }

    /// <summary>The largest end after <paramref name="start"/> whose piece counts within the limit; at least one character (or surrogate pair).</summary>
    private int LongestFit(string content, int start)
    {
        bool Fits(int end) => _tokenizer.CountTokens(content.AsSpan(start, end - start)) <= _chunkTokens;

        var good = NextBoundary(content, start);
        if (good >= content.Length || !Fits(good))
        {
            return good;
        }

        // Gallop from a guess of a few characters per token, then bisect between the last fit and the first miss.
        var step = Math.Max(1, _chunkTokens * 4);
        var bad = -1;
        while (bad < 0)
        {
            var probe = Boundary(content, Math.Min(content.Length, good + step), good);
            if (Fits(probe))
            {
                if (probe >= content.Length)
                {
                    return probe;
                }

                good = probe;
                step *= 2;
            }
            else
            {
                bad = probe;
            }
        }

        while (bad - good > 1)
        {
            var middle = Boundary(content, good + (bad - good) / 2, good);
            if (middle <= good || middle >= bad)
            {
                break;
            }

            if (Fits(middle))
            {
                good = middle;
            }
            else
            {
                bad = middle;
            }
        }

        return good;
    }

    /// <summary>
    /// The smallest start in <c>(start, end]</c> whose piece up to <paramref name="end"/> counts at most
    /// <paramref name="overlapTokens"/>, moved forward to a word start so the overlap does not begin mid-word.
    /// </summary>
    private int OverlapStart(string content, int start, int end, int overlapTokens)
    {
        bool Fits(int from) => _tokenizer.CountTokens(content.AsSpan(from, end - from)) <= overlapTokens;

        var low = NextBoundary(content, start);
        var high = end;
        while (low < high)
        {
            var middle = Boundary(content, low + (high - low) / 2, low);
            if (middle >= high)
            {
                break;
            }

            if (Fits(middle))
            {
                high = middle;
            }
            else
            {
                low = NextBoundary(content, middle);
            }
        }

        var word = high;
        while (word < end && word > 0 && !char.IsWhiteSpace(content[word - 1]))
        {
            word++;
        }

        return word < end ? word : high;
    }

    /// <summary><paramref name="end"/> moved back to just before the last whitespace in the second half of the piece, when there is one.</summary>
    private static int BackToWhiteSpace(string content, int start, int end)
    {
        for (var i = end; i > start + (end - start) / 2; i--)
        {
            if (char.IsWhiteSpace(content[i - 1]))
            {
                return i - 1;
            }
        }

        return end;
    }

    private static int SkipWhiteSpace(string content, int index)
    {
        while (index < content.Length && char.IsWhiteSpace(content[index]))
        {
            index++;
        }

        return index;
    }

    /// <summary>The cut after the character (or surrogate pair) at <paramref name="index"/>.</summary>
    private static int NextBoundary(string content, int index) =>
        index + (char.IsHighSurrogate(content[index]) && index + 1 < content.Length && char.IsLowSurrogate(content[index + 1]) ? 2 : 1);

    /// <summary>
    /// <paramref name="index"/>, moved off the middle of a surrogate pair: back, unless that would reach
    /// <paramref name="floor"/>, else forward.
    /// </summary>
    private static int Boundary(string content, int index, int floor)
    {
        if (index >= content.Length || index == 0 || !char.IsLowSurrogate(content[index]) || !char.IsHighSurrogate(content[index - 1]))
        {
            return index;
        }

        return index - 1 > floor ? index - 1 : index + 1;
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
