using System.Text;
using Microsoft.ML.Tokenizers;
using Nachos.Core.Llm;
using Nachos.Core.Memory;
using Nachos.Core.Tokens;
using Shouldly;

namespace Nachos.Core.Tests.Memory;

public sealed class MessageChunkerTests
{
    private static readonly EmbeddingChunking Chunking = EmbeddingChunking.Cl100kBase();
    private static readonly string[] Words =
        ["apple", "river", "quiet", "sun", "market", "paper", "lantern", "grow", "slowly", "garden", "iron", "cloud", "seven"];

    private static string Text(int words, int seed)
    {
        var random = new Random(seed);
        return string.Join(' ', Enumerable.Range(0, words).Select(_ => Words[random.Next(Words.Length)]));
    }

    private static int Tokens(string text) => Chunking.Tokenizer.CountTokens(text);

    private static bool HasNoSplitSurrogate(string chunk) =>
        chunk.Length > 0 && !char.IsLowSurrogate(chunk[0]) && !char.IsHighSurrogate(chunk[^1]);

    [Fact]
    public void ShortContent_OneChunk()
    {
        new MessageChunker(Chunking, 512).Chunk("hello there, how are you?").ShouldBe(["hello there, how are you?"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    public void Blank_NoChunks(string content)
    {
        new MessageChunker(Chunking, 512).Chunk(content).ShouldBeEmpty();
    }

    [Fact]
    public void LongContent_OverlapIsTwentyPercent()
    {
        var chunker = new MessageChunker(Chunking, 50);
        var content = Text(300, seed: 7);

        var chunks = chunker.Chunk(content);

        chunks.Count.ShouldBeGreaterThan(2);
        for (var i = 0; i + 1 < chunks.Count; i++)
        {
            var current = Chunking.Tokenizer.EncodeToIds(chunks[i]);
            var next = Chunking.Tokenizer.EncodeToIds(chunks[i + 1]);
            current.Count.ShouldBe(50);
            // 20% of 50 tokens: the next chunk starts with the last ten tokens of this one.
            next.Take(10).ShouldBe(current.Skip(40));
        }

        chunks[0].ShouldBe(content[..chunks[0].Length]);
        content.ShouldEndWith(chunks[^1]);
    }

    [Fact]
    public void SpecialTokenText_IsSizedAsOrdinaryText()
    {
        const string special = "<|endoftext|>";
        Tokens(special).ShouldBeGreaterThan(1);
        var content = string.Join(' ', Enumerable.Repeat(special, 30));

        var chunks = new MessageChunker(Chunking, 20).Chunk(content);

        chunks.Count.ShouldBeGreaterThan(1);
        chunks.ShouldAllBe(chunk => Tokens(chunk) <= 20);
    }

    [Fact]
    public void ChangedVocabularyBytes_FailClosed()
    {
        Should.Throw<InvalidOperationException>(() => Cl100kBaseTokenizer.Create(new MemoryStream([1, 2, 3])));
        Should.Throw<InvalidOperationException>(() => Cl100kBaseTokenizer.Create(null));
    }

    [Fact]
    public void ModelLimitCapsConfiguredChunkTokens()
    {
        var chunker = new MessageChunker(new EmbeddingChunking(Chunking.Tokenizer, 30), 512);

        chunker.Chunk(Text(200, seed: 3)).ShouldAllBe(chunk => Tokens(chunk) <= 30);
    }

    /// <summary>
    /// A small uncased WordPiece vocabulary with the catalog models' tokenizer settings: every lower-case word tokenizes
    /// (letters and continuation letters), accents are stripped and CJK characters are split, as with the real vocab.txt.
    /// </summary>
    private static BertTokenizer LocalTokenizer()
    {
        var letters = Enumerable.Range('a', 26).Select(c => ((char)c).ToString()).ToList();
        string[] vocabulary =
        [
            "[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]", ".", ",", "日", "本", "語",
            .. Words, "cafe", "uber", .. letters, .. letters.Select(letter => "##" + letter),
        ];
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', vocabulary)));
        return BertTokenizer.Create(stream, new BertOptions { LowerCaseBeforeTokenization = true, RemoveNonSpacingMarks = true, IndividuallyTokenizeCjk = true });
    }

    private static string MixedText(int seed, int words)
    {
        var random = new Random(seed);
        var content = new StringBuilder();
        for (var i = 0; i < words; i++)
        {
            content.Append(random.Next(7) switch
            {
                0 => "Café Über",
                1 => "日本語テキスト",
                2 => "🙂🎉",
                3 => "Café RIVER.",
                4 => "end.\n\n",
                5 => Words[random.Next(Words.Length)].ToUpperInvariant(),
                _ => Words[random.Next(Words.Length)],
            }).Append(' ');
        }

        return content.ToString();
    }

    [Fact]
    public void LocalModel_ChunksWithinModelMaxTokens()
    {
        var tokenizer = LocalTokenizer();
        var model = LocalEmbeddingModel.AllMiniLmL6V2;
        var chunker = new MessageChunker(new EmbeddingChunking(tokenizer, model.MaxContentTokens), chunkTokens: 512);
        var content = MixedText(seed: 11, words: 1500);

        var chunks = chunker.Chunk(content);

        chunks.Count.ShouldBeGreaterThan(3);
        chunks.ShouldAllBe(chunk => tokenizer.CountTokens(chunk) <= 254);
        content.ShouldStartWith(chunks[0]);
        content.TrimEnd().ShouldEndWith(chunks[^1].TrimEnd());
        var searchFrom = 0;
        var previousEnd = 0;
        foreach (var chunk in chunks)
        {
            // Cut from the original text, so case, accents and spacing survive; and each overlaps the one before.
            var at = content.IndexOf(chunk, searchFrom, StringComparison.Ordinal);
            at.ShouldBeGreaterThanOrEqualTo(0);
            if (previousEnd > 0)
            {
                at.ShouldBeLessThan(previousEnd);
                tokenizer.CountTokens(content.AsSpan(at, previousEnd - at)).ShouldBeInRange(1, 254 / 5);
            }

            searchFrom = at + 1;
            previousEnd = at + chunk.Length;
        }

        chunks.ShouldContain(chunk => chunk.Contains("Café", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1, 8, 200)]
    [InlineData(2, 30, 400)]
    [InlineData(3, 254, 3000)]
    [InlineData(4, 1, 40)]
    [InlineData(5, 4, 100)]
    public void LocalModel_EveryChunkIsOriginalTextWithinLimit(int seed, int limit, int words)
    {
        var tokenizer = LocalTokenizer();
        var chunker = new MessageChunker(new EmbeddingChunking(tokenizer, limit), chunkTokens: 512);
        var content = MixedText(seed, words);

        var chunks = chunker.Chunk(content);

        chunks.ShouldNotBeEmpty();
        chunks.ShouldAllBe(chunk => tokenizer.CountTokens(chunk) <= limit);
        chunks.ShouldAllBe(chunk => content.Contains(chunk, StringComparison.Ordinal));
        chunks.ShouldAllBe(chunk => HasNoSplitSurrogate(chunk));
    }

    [Theory]
    [InlineData(1, 40, 50)]
    [InlineData(2, 64, 300)]
    [InlineData(3, 512, 2000)]
    [InlineData(4, 7, 80)]
    [InlineData(5, 8, 120)]
    public void EveryChunkWithinTokenLimit(int seed, int limit, int words)
    {
        var chunker = new MessageChunker(Chunking, limit);
        var random = new Random(seed);
        var content = new StringBuilder();
        for (var i = 0; i < words; i++)
        {
            // Mixed scripts, emoji (surrogate pairs), digits and punctuation, so cuts land in awkward places.
            content.Append(random.Next(6) switch
            {
                0 => "héllo wörld",
                1 => "日本語のテキスト",
                2 => "🙂🎉",
                3 => random.Next(100000).ToString(System.Globalization.CultureInfo.InvariantCulture),
                4 => "end.\n\n",
                _ => Words[random.Next(Words.Length)],
            }).Append(' ');
        }

        var chunks = chunker.Chunk(content.ToString());

        chunks.ShouldNotBeEmpty();
        chunks.ShouldAllBe(chunk => Tokens(chunk) <= limit);
        chunks.ShouldAllBe(chunk => HasNoSplitSurrogate(chunk));
    }
}
