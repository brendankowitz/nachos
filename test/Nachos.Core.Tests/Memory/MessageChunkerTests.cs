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
