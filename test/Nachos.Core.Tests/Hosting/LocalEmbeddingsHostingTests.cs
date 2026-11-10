using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nachos.Core.Configuration;
using Nachos.Core.Llm;
using Nachos.Hosting.LocalEmbeddings;
using Shouldly;

namespace Nachos.Core.Tests.Hosting;

/// <summary>Local-embedding registration and file checks that need no model on disk.</summary>
public sealed class LocalEmbeddingsHostingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nachos-local-models-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddNachos(builder => builder.UseInMemory().UseConfiguredAI(configuration));
        services.Configure<NachosOptions>(configuration.GetSection("Nachos"));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void UnknownEmbeddingsSetting_FailsAtRegistration()
    {
        var error = Should.Throw<InvalidOperationException>(() => Build(new() { ["Nachos:Ai:Embeddings"] = "Remote" }));

        error.Message.ShouldBe("Nachos:Ai:Embeddings must be 'Local' or unset.");
    }

    [Fact]
    public void ExplicitDedupMaxDistance_BeatsTheModelDefault()
    {
        using var defaults = Build(new() { ["Nachos:Ai:Embeddings"] = "Local", ["Embeddings:Local:Model"] = "all-minilm-l6-v2" });
        using var explicitly = Build(new() { ["Nachos:Ai:Embeddings"] = "Local", ["Nachos:Deriver:DedupMaxDistance"] = "0.02" });

        defaults.GetRequiredService<IOptions<NachosOptions>>().Value.Deriver.DedupMaxDistance.ShouldBe(LocalEmbeddingModel.AllMiniLmL6V2.DedupMaxDistance);
        explicitly.GetRequiredService<IOptions<NachosOptions>>().Value.Deriver.DedupMaxDistance.ShouldBe(0.02);
    }

    [Fact]
    public void UnknownModel_FailsValidation()
    {
        using var provider = Build(new() { ["Nachos:Ai:Embeddings"] = "Local", ["Embeddings:Local:Model"] = "gpt-embed" });

        var error = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<LocalEmbeddingOptions>>().Value);

        error.Message.ShouldContain("bge-small-en-v1.5, all-minilm-l6-v2");
    }

    [Fact]
    public void EveryEmbeddingGoesThroughTheGate()
    {
        using var provider = Build([]);

        provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>().GetService<GatedEmbeddingGenerator>().ShouldNotBeNull();
        provider.GetRequiredService<EmbeddingSpaceGate>().IsOpen.ShouldBeTrue();
    }

    [Fact]
    public void MissingFiles_FailWithTheFetchCommand()
    {
        var error = Should.Throw<InvalidOperationException>(() => LocalEmbeddingGenerator.Create(LocalEmbeddingModel.Default, _directory, 32));

        error.Message.ShouldContain($"nachos models fetch bge-small-en-v1.5 --dir \"{_directory}\"");
    }

    [Theory]
    [InlineData(null, "1")]
    [InlineData("0", "0")]
    public void Create_TurnsOnnxRuntimeTelemetryOff_UnlessTheOperatorSetIt(string? before, string after)
    {
        var saved = Environment.GetEnvironmentVariable(LocalEmbeddingGenerator.TelemetryVariable);
        try
        {
            Environment.SetEnvironmentVariable(LocalEmbeddingGenerator.TelemetryVariable, before);

            Should.Throw<InvalidOperationException>(() => LocalEmbeddingGenerator.Create(LocalEmbeddingModel.Default, _directory, 32));

            Environment.GetEnvironmentVariable(LocalEmbeddingGenerator.TelemetryVariable).ShouldBe(after);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LocalEmbeddingGenerator.TelemetryVariable, saved);
        }
    }

    [Fact]
    public void ChangedFile_FailsWithTheHashMismatch()
    {
        var folder = LocalEmbeddingModel.Default.FolderIn(_directory);
        Directory.CreateDirectory(folder);
        foreach (var file in LocalEmbeddingModel.Default.Files)
        {
            File.WriteAllText(Path.Combine(folder, file.Name), "not the pinned file");
        }

        var error = Should.Throw<InvalidOperationException>(() => LocalEmbeddingGenerator.Create(LocalEmbeddingModel.Default, _directory, 32));

        error.Message.ShouldContain("its SHA-256 differs");
    }

    [Fact]
    public void Catalog_PinsHttpsRevisionsAndHashes()
    {
        foreach (var model in LocalEmbeddingModel.All)
        {
            model.Files.Select(file => file.Name).ShouldBe([LocalEmbeddingModel.ModelFileName, LocalEmbeddingModel.VocabularyFileName]);
            model.Files.ShouldAllBe(file => file.Source.Scheme == Uri.UriSchemeHttps && !file.Source.AbsolutePath.Contains("/resolve/main/"));
            model.Files.ShouldAllBe(file => file.Sha256.Length == 64 && file.Sha256.All(c => char.IsAsciiHexDigitLower(c) || char.IsAsciiDigit(c)));
        }
    }
}
