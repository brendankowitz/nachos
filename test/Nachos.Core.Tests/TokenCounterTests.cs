using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Core.Configuration;
using Nachos.Core.Tokens;
using Nachos.Core.Validation;
using Shouldly;

namespace Nachos.Core.Tests;

public sealed class TokenCounterTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("hello world", 2)]
    [InlineData("你好世界", 2)]
    [InlineData("お誕生日おめでとう", 8)]
    [InlineData("234927112986", 4)]
    [InlineData("679872123313", 4)]
    public void KnownStrings_UseO200kBase(string text, int expected)
    {
        var counter = new TiktokenTokenCounter();

        counter.Count(text).ShouldBe(expected);
    }

    [Fact]
    public void NullIsNotSilentlyCountedAsEmptyContent()
    {
        Should.Throw<ArgumentNullException>(() => new TiktokenTokenCounter().Count(null!));
    }

    [Theory]
    [InlineData(1, 7)]
    [InlineData(2, 13)]
    [InlineData(10, 61)]
    [InlineData(100, 601)]
    public void SpecialSpellings_AreOrdinaryText(int repetitions, int expected)
    {
        // Independent encode_ordinary results from the approved tiktoken 0.14.0 adjudication.
        var text = string.Concat(Enumerable.Repeat("<|endoftext|>", repetitions));

        new TiktokenTokenCounter().Count(text).ShouldBe(expected);
    }

    [Fact]
    public void SpecialSpellings_RemainOrdinaryAfterCacheEviction()
    {
        var counter = new TiktokenTokenCounter();
        var text = string.Concat(Enumerable.Repeat("<|endoftext|>", 10));
        _ = counter.Count(text);

        for (var index = 0; index < 8200; index++)
        {
            _ = counter.Count(ShortPiece(index));
        }

        counter.Count(text).ShouldBe(61);
        counter.Count(text).ShouldBe(61);
    }

    [Theory]
    [InlineData("C1", 1916)]
    [InlineData("C2", 125)]
    [InlineData("C3", 511)]
    [InlineData("C4", 215)]
    [InlineData("W1", 253)]
    [InlineData("P1", 660)]
    public void FrozenOrdinaryCorpora_PreserveAcceptedCounts(string corpus, int expected)
    {
        // Frozen bundle out/bench.txt: scan, regex and Microsoft agree on these 1k inputs.
        new TiktokenTokenCounter().Count(ReadCorpus(corpus)).ShouldBe(expected);
    }

    [Fact]
    public void EmbeddedVocabulary_MatchesPinnedCanonicalO200kBase()
    {
        using var resource = Assembly.Load("Microsoft.ML.Tokenizers.Data.O200kBase")
            .GetManifestResourceStream("o200k_base.tiktoken.deflate");
        resource.ShouldNotBeNull();
        Convert.ToHexString(SHA256.HashData(resource)).ShouldBe(
            "88B2A54DCEDC68D39B1AF4B8DC744ADBA8ECA01310B7D07A687F1ADD3BE75524");
        resource.Position = 0;
        using var deflate = new DeflateStream(resource, CompressionMode.Decompress);
        using var reader = new StreamReader(deflate);
        reader.ReadLine().ShouldBe("Capacity: 199999");
        using var canonical = new MemoryStream();
        using var writer = new StreamWriter(canonical, new UTF8Encoding(false), leaveOpen: true);
        var rank = 0;
        while (reader.ReadLine() is { } token)
        {
            writer.Write(token);
            writer.Write(' ');
            writer.Write(rank.ToString(CultureInfo.InvariantCulture));
            writer.Write('\n');
            rank++;
        }
        writer.Flush();

        rank.ShouldBe(199998);
        Convert.ToHexString(SHA256.HashData(canonical.ToArray())).ShouldBe(
            "446A9538CB6C348E3516120D7C08B09F57C36495E2ACFFFE59A5BF8B0CFB1A2D");
    }

    [Fact]
    public async Task SharedTokenizer_OrdinaryCountsSurviveTwentyConcurrentEvictionRounds()
    {
        const int workers = 8;
        const int rounds = 20;
        const int piecesPerWorker = 1025;
        var counter = new TiktokenTokenCounter();
        var text = string.Concat(Enumerable.Repeat("<|endoftext|>", 10));
        using var barrier = new Barrier(workers);
        var tasks = Enumerable.Range(0, workers).Select(worker => Task.Factory.StartNew(() =>
        {
            var counts = new List<int>();
            for (var round = 0; round < rounds; round++)
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(30)).ShouldBeTrue();
                counts.Add(counter.Count(text));
                for (var index = 0; index < piecesPerWorker; index++)
                {
                    _ = counter.Count(ShortPiece((round * workers + worker) * piecesPerWorker + index));
                }
                barrier.SignalAndWait(TimeSpan.FromSeconds(30)).ShouldBeTrue();
                counts.Add(counter.Count(text));
                counts.Add(counter.Count("hello world"));
                counts.Add(counter.Count("お誕生日おめでとう"));
            }
            return counts;
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        var results = await Task.WhenAll(tasks);
        foreach (var counts in results)
        {
            counts.Count.ShouldBe(rounds * 4);
            for (var round = 0; round < rounds; round++)
            {
                counts[round * 4].ShouldBe(61);
                counts[round * 4 + 1].ShouldBe(61);
                counts[round * 4 + 2].ShouldBe(2);
                counts[round * 4 + 3].ShouldBe(8);
            }
        }
    }

    [Theory]
    [InlineData(2000, false)]
    [InlineData(2001, true)]
    public void RealTokenCounts_EnforceTheInstructionBoundary(int tokens, bool reject)
    {
        var counter = new TiktokenTokenCounter();
        var instructions = string.Join(" ", Enumerable.Repeat("hello", tokens));
        var validator = new RequestValidator(Options.Create(new NachosOptions()), counter);
        var message = new MessageCreate("message", "peer",
            Configuration: new(new(CustomInstructions: instructions)));

        counter.Count(instructions).ShouldBe(tokens);
        if (reject)
        {
            Should.Throw<NachosValidationException>(() => validator.ValidateMessages([message]));
        }
        else
        {
            Should.NotThrow(() => validator.ValidateMessages([message]));
        }
    }

    [Fact]
    public async Task SharedTokenizer_CountsDeterministicallyUnderConcurrency()
    {
        var counter = new TiktokenTokenCounter();
        var results = await Task.WhenAll(Enumerable.Range(0, 128).Select(index =>
            Task.Run(() => index % 2 == 0 ? counter.Count("hello world") : counter.Count("お誕生日おめでとう"))));

        for (var index = 0; index < results.Length; index++)
        {
            results[index].ShouldBe(index % 2 == 0 ? 2 : 8);
        }
    }

    private static string ShortPiece(int index)
    {
        // Unique five-letter ordinary pretokens, each small enough for the default LRU.
        return string.Create(5, index, static (characters, value) =>
        {
            characters[0] = 'q';
            for (var position = 1; position < characters.Length; position++)
            {
                characters[position] = (char)('a' + value % 26);
                value /= 26;
            }
        });
    }

    private static string ReadCorpus(string corpus)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "Nachos.slnx")))
            {
                continue;
            }

            using var file = File.OpenRead(Path.Combine(directory.FullName, "research", "token-feasibility",
                "token-feasibility-bundle.tar.gz"));
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            while (tar.GetNextEntry() is { } entry)
            {
                if (entry.Name == $"token-feasibility/data/corpora/{corpus}_1000.txt")
                {
                    using var reader = new StreamReader(entry.DataStream!);
                    return reader.ReadToEnd();
                }
            }
            throw new InvalidOperationException($"Frozen corpus {corpus} was not found.");
        }
        throw new InvalidOperationException("Nachos.slnx was not found above the Core test output.");
    }
}
