using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nachos.Core.Configuration;
using Nachos.Core.Deriver;
using Nachos.Core.Llm;
using Nachos.Core.Worker;
using Shouldly;

namespace Nachos.Core.Tests.Hosting;

public sealed class ConfiguredAITests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings, ILoggerProvider? logs = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging(logging => _ = logs is null ? logging : logging.AddProvider(logs));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddNachos(builder => builder.UseInMemory().UseConfiguredAI(configuration).AddNachosWorker());
        services.Configure<NachosOptions>(configuration.GetSection("Nachos"));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [Fact]
    public void WithoutAnEndpoint_UsesFakeAI_AndTheDeriverIsEager()
    {
        using var provider = Build([]);

        provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>().ShouldBeOfType<DeterministicEmbeddingGenerator>();
        var deriver = provider.GetRequiredService<IOptions<NachosOptions>>().Value.Deriver;
        deriver.EligibilityTokens.ShouldBe(1);
        deriver.MaxAge.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void ExplicitDeriverSettings_BeatTheEagerFakeAIDefaults()
    {
        using var provider = Build(new() { ["Nachos:Deriver:EligibilityTokens"] = "512", ["Nachos:Deriver:MaxAge"] = "00:30:00" });

        var deriver = provider.GetRequiredService<IOptions<NachosOptions>>().Value.Deriver;
        deriver.EligibilityTokens.ShouldBe(512);
        deriver.MaxAge.ShouldBe(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void ModeFake_BeatsAnEndpoint()
    {
        using var provider = Build(new() { ["AzureOpenAI:Endpoint"] = "https://example.openai.azure.com/", ["Nachos:Ai:Mode"] = "Fake" });

        provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>().ShouldBeOfType<DeterministicEmbeddingGenerator>();
    }

    [Fact]
    public void WithAnEndpoint_UsesAzureOpenAI_AndKeepsTheLazyDeriverDefaults()
    {
        using var provider = Build(new()
        {
            ["AzureOpenAI:Endpoint"] = "https://example.openai.azure.com/",
            ["AzureOpenAI:EmbeddingDeployment"] = "embed",
        });

        provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>().ShouldNotBeOfType<DeterministicEmbeddingGenerator>();
        provider.GetRequiredService<IOptions<NachosOptions>>().Value.Deriver.EligibilityTokens.ShouldBe(new DeriverOptions().EligibilityTokens);
    }

    [Fact]
    public async Task AHostWithoutAnEmbeddingGenerator_WarnsAtStartup()
    {
        var logs = new CapturingProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(logs));
        services.AddNachos(builder => builder.UseInMemory());
        await using var provider = services.BuildServiceProvider();

        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            await hosted.StartAsync(CancellationToken.None);
        }

        logs.Warnings.Count(w => w.Contains("No embedding generator is registered", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task AHostWithFakeAI_WarnsThatItIsNotAMemoryQualityMode()
    {
        var logs = new CapturingProvider();
        using var provider = Build([], logs);

        foreach (var hosted in provider.GetServices<IHostedService>().Where(h => h is not (QueueConsumerService or ReconcilerService)))
        {
            await hosted.StartAsync(CancellationToken.None);
        }

        logs.Warnings.Count(w => w.Contains("Fake AI is active", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public void AHostWithAzureOpenAI_DoesNotRegisterTheFakeAIWarning()
    {
        var logs = new CapturingProvider();
        using var provider = Build(
            new() { ["AzureOpenAI:Endpoint"] = "https://example.openai.azure.com/", ["AzureOpenAI:EmbeddingDeployment"] = "embed" }, logs);

        provider.GetServices<IHostedService>().Select(h => h.GetType().Name).ShouldNotContain("FakeAiWarning");
    }

    [Fact]
    public async Task AHostWithFakeAI_DoesNotWarn()
    {
        var logs = new CapturingProvider();
        using var provider = Build([], logs);

        foreach (var hosted in provider.GetServices<IHostedService>().Where(h => h is not (QueueConsumerService or ReconcilerService)))
        {
            await hosted.StartAsync(CancellationToken.None);
            await hosted.StopAsync(CancellationToken.None);
        }

        logs.Warnings.ShouldNotContain(w => w.Contains("No embedding generator", StringComparison.Ordinal));
    }

    private sealed class CapturingProvider : ILoggerProvider
    {
        public List<string> Warnings { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Capture(Warnings);

        public void Dispose()
        {
        }

        private sealed class Capture(List<string> warnings) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning)
                {
                    warnings.Add(formatter(state, exception));
                }
            }
        }
    }
}
