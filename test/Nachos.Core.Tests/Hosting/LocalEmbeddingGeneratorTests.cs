using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Core.Configuration;
using Nachos.Core.Llm;
using Nachos.Core.Memory;
using Nachos.Hosting.LocalEmbeddings;
using Shouldly;

namespace Nachos.Core.Tests.Hosting;

/// <summary>
/// The real models, run through ONNX Runtime. They skip unless <c>NACHOS_LOCAL_MODEL_DIR</c> names a directory holding both
/// catalog models (<c>nachos models fetch</c>); CI fetches them and sets it.
/// </summary>
public sealed class LocalEmbeddingGeneratorTests
{
    public static TheoryData<string> Models => [.. LocalEmbeddingModel.All.Select(model => model.Id)];

    private static LocalEmbeddingGenerator Create(string model) =>
        LocalEmbeddingGenerator.Create(LocalEmbeddingModel.Find(model)!, RequiresLocalModelTheoryAttribute.Directory!, batchSize: 4);

    private static async Task<float[]> EmbedAsync(LocalEmbeddingGenerator generator, string text) =>
        (await generator.GenerateAsync([text]))[0].Vector.ToArray();

    private static double Distance(float[] a, float[] b) => 1 - a.Zip(b, (x, y) => (double)x * y).Sum();

    [RequiresLocalModelTheory]
    [MemberData(nameof(Models))]
    public async Task Output1536_UnitNorm_PaddingZero(string model)
    {
        using var generator = Create(model);

        var vector = await EmbedAsync(generator, "Bob prefers dark roast coffee.");

        vector.Length.ShouldBe(VectorSpace.Dimensions);
        Math.Sqrt(vector.Sum(value => (double)value * value)).ShouldBe(1, tolerance: 1e-5);
        vector.Skip(LocalEmbeddingModel.Find(model)!.Dimensions).ShouldAllBe(value => value == 0);
        vector.Take(LocalEmbeddingModel.Find(model)!.Dimensions).Count(value => value != 0).ShouldBeGreaterThan(300);
    }

    [RequiresLocalModelTheory]
    [MemberData(nameof(Models))]
    public async Task Paraphrase_CloserThanUnrelated(string model)
    {
        using var generator = Create(model);
        var vectors = await generator.GenerateAsync(
            ["Alice lives in Seattle.", "Alice's home is in Seattle.", "The quarterly report is due on Friday."]);

        Distance(vectors[0].Vector.ToArray(), vectors[1].Vector.ToArray())
            .ShouldBeLessThan(Distance(vectors[0].Vector.ToArray(), vectors[2].Vector.ToArray()));
    }

    [RequiresLocalModelTheory]
    [MemberData(nameof(Models))]
    public async Task LongInput_TruncatedNotThrown(string model)
    {
        using var generator = Create(model);
        var longText = string.Join(' ', Enumerable.Repeat("The river runs past the old mill and the market square.", 400));

        var truncated = await EmbedAsync(generator, longText);
        var longer = await EmbedAsync(generator, longText + " Then something entirely different happens at the very end.");

        Math.Sqrt(truncated.Sum(value => (double)value * value)).ShouldBe(1, tolerance: 1e-5);
        // Everything past the model's limit is cut off, so the extra sentence changes nothing.
        Distance(truncated, longer).ShouldBe(0, tolerance: 1e-6);
    }

    [RequiresLocalModelTheory]
    [MemberData(nameof(Models))]
    public async Task Deterministic_AcrossCalls(string model)
    {
        using var generator = Create(model);
        const string Text = "Grace speaks fluent Spanish.";

        var first = await EmbedAsync(generator, Text);
        var second = await EmbedAsync(generator, Text);
        // In a batch the text is padded to a longer neighbour's length; the attention mask must make that invisible.
        var batched = (await generator.GenerateAsync(
            [Text, string.Join(' ', Enumerable.Repeat("padding makes this input much longer", 20)), "x"]))[0].Vector.ToArray();

        second.ShouldBe(first);
        Distance(first, batched).ShouldBe(0, tolerance: 1e-5);
    }

    /// <summary>
    /// Reference values from the same pinned files run with Hugging Face <c>tokenizers</c> (the models' tokenizer.json) and
    /// Python onnxruntime; see research/local-embeddings-calibration.md. They pin the tokenizer settings (lower-casing,
    /// accent stripping, CJK splitting) and the pooling.
    /// </summary>
    [RequiresLocalModelTheory]
    [InlineData("bge-small-en-v1.5", new[] { 0.003398, 0.037437, -0.014255, 0.016646, 0.029501, -0.053571, -0.006984, 0.053195 })]
    [InlineData("all-minilm-l6-v2", new[] { 0.023424, 0.025649, 0.016590, 0.030706, 0.062560, 0.026866, 0.024313, 0.042838 })]
    public async Task MatchesTheReferenceImplementation(string model, double[] expectedPrefix)
    {
        using var generator = Create(model);

        var vector = await EmbedAsync(generator, "Café owners in Zürich prefer espresso.");
        var ids = generator.Chunking.Tokenizer.EncodeToIds("日本語のテキスト and ÉLAN");

        vector.Take(8).Select(value => (double)value).ToArray().ShouldBe(expectedPrefix, tolerance: 1e-4);
        ids.ShouldBe([1864, 1876, 1950, 1671, 30239, 30227, 30233, 30240, 1998, 3449, 2319]);
    }

    [RequiresLocalModelTheory]
    [MemberData(nameof(Models))]
    public async Task DedupCalibration_CatalogThreshold_MergesNoDistinctPair(string model)
    {
        using var generator = Create(model);
        var threshold = LocalEmbeddingModel.Find(model)!.DedupMaxDistance;
        var pairs = DedupPairs();

        var distances = new List<(bool Paraphrase, double Distance)>();
        foreach (var (a, b, paraphrase) in pairs)
        {
            var vectors = await generator.GenerateAsync([a, b]);
            distances.Add((paraphrase, Distance(vectors[0].Vector.ToArray(), vectors[1].Vector.ToArray())));
        }

        distances.Where(pair => !pair.Paraphrase).ShouldAllBe(pair => pair.Distance > threshold);
        distances.Count(pair => pair.Paraphrase && pair.Distance <= threshold).ShouldBeGreaterThan(0);
    }

    [RequiresLocalModelTheory]
    [MemberData(nameof(Models))]
    public async Task ConfiguredLocal_StampsTheModel_ChunksWithItsTokenizer_AndUsesItsDedupThreshold(string model)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Nachos:Ai:Mode"] = "Fake",
            ["Nachos:Ai:Embeddings"] = "Local",
            ["Embeddings:Local:Model"] = model,
            ["Embeddings:Local:ModelDirectory"] = RequiresLocalModelTheoryAttribute.Directory,
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddNachos(builder => builder.UseInMemory().UseConfiguredAI(configuration));
        services.Configure<NachosOptions>(configuration.GetSection("Nachos"));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var entry = LocalEmbeddingModel.Find(model)!;

        foreach (var hosted in provider.GetServices<IHostedService>().OfType<EmbeddingSpaceCheck>())
        {
            await hosted.StartAsync(CancellationToken.None);
        }

        (await provider.GetRequiredService<IMemoryStore>().Embeddings.GetSpaceAsync(default)).ShouldBe(new EmbeddingSpace(model, 384));
        provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>().GetService<LocalEmbeddingGenerator>().ShouldNotBeNull();
        provider.GetRequiredService<EmbeddingChunking>().MaxTokens.ShouldBe(entry.MaxContentTokens);
        var chunks = provider.GetRequiredService<MessageChunker>().Chunk(string.Join(' ', Enumerable.Repeat("Alice met Bob at the Café.", 300)));
        chunks.Count.ShouldBeGreaterThan(1);
        chunks.ShouldAllBe(chunk => chunk.Contains("Café", StringComparison.Ordinal));
        provider.GetRequiredService<IOptions<NachosOptions>>().Value.Deriver.DedupMaxDistance.ShouldBe(entry.DedupMaxDistance);
    }

    private static List<(string A, string B, bool Paraphrase)> DedupPairs()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "test", "fixtures", "dedup-pairs.json");
            if (File.Exists(Path.Combine(directory.FullName, "Nachos.slnx")) && File.Exists(path))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                return
                [
                    .. json.RootElement.GetProperty("pairs").EnumerateArray().Select(pair => (
                        pair.GetProperty("a").GetString()!, pair.GetProperty("b").GetString()!, pair.GetProperty("paraphrase").GetBoolean())),
                ];
            }
        }

        throw new InvalidOperationException("test/fixtures/dedup-pairs.json was not found above the Core test output.");
    }
}
