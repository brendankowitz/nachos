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
    private static ServiceProvider Build(
        Dictionary<string, string?> settings, ILoggerProvider? logs = null, string environment = "Development")
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging(logging => _ = logs is null ? logging : logging.AddProvider(logs));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new TestEnvironment(environment));
        services.AddNachos(builder => builder.UseInMemory().UseConfiguredAI(configuration).AddNachosWorker());
        services.Configure<NachosOptions>(configuration.GetSection("Nachos"));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static bool IsFake(IServiceProvider provider) =>
        provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>().GetService<DeterministicEmbeddingGenerator>() is not null;

    private static async Task StartAsync(IServiceProvider provider)
    {
        foreach (var hosted in provider.GetServices<IHostedService>().Where(h => h is not (QueueConsumerService or ReconcilerService)))
        {
            await hosted.StartAsync(CancellationToken.None);
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void WithoutAnEndpoint_UsesFakeAI_AndTheDeriverIsEager()
    {
        using var provider = Build([]);

        IsFake(provider).ShouldBeTrue();
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

        IsFake(provider).ShouldBeTrue();
    }

    [Fact]
    public void WithAnEndpoint_UsesAzureOpenAI_AndKeepsTheLazyDeriverDefaults()
    {
        using var provider = Build(new()
        {
            ["AzureOpenAI:Endpoint"] = "https://example.openai.azure.com/",
            ["AzureOpenAI:EmbeddingDeployment"] = "embed",
        });

        IsFake(provider).ShouldBeFalse();
        provider.GetRequiredService<IOptions<NachosOptions>>().Value.Deriver.EligibilityTokens.ShouldBe(new DeriverOptions().EligibilityTokens);
    }

    [Fact]
    public void WithAnEndpointAndDeployments_AndNoLlmSection_TheDeriverGetsAChatClient()
    {
        using var provider = Build(new()
        {
            ["AzureOpenAI:Endpoint"] = "https://example.openai.azure.com/",
            ["AzureOpenAI:ChatDeployment"] = "chat",
            ["AzureOpenAI:EmbeddingDeployment"] = "embed",
        });

        // Building the pipeline makes no request; it failed before because no "default" profile existed.
        provider.GetRequiredService<NachosChatClients>().ForAgent(DeriverService.AgentName).ShouldNotBeNull();
        provider.GetRequiredService<IOptions<LlmOptions>>().Value.Profiles[LlmOptions.DefaultProfileName].Model.ShouldBeNull();
    }

    [Fact]
    public void AnExplicitDefaultProfile_IsKept()
    {
        using var provider = Build(new()
        {
            ["AzureOpenAI:Endpoint"] = "https://example.openai.azure.com/",
            ["AzureOpenAI:EmbeddingDeployment"] = "embed",
            ["Llm:Profiles:default:Model"] = "gpt-custom",
        });

        provider.GetRequiredService<IOptions<LlmOptions>>().Value.Profiles[LlmOptions.DefaultProfileName].Model.ShouldBe("gpt-custom");
    }

    [Fact]
    public void TheEmbeddingGenerator_IsInstrumented()
    {
        using var provider = Build([]);

        provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>()
            .GetService<OpenTelemetryEmbeddingGenerator<string, Embedding<float>>>().ShouldNotBeNull();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task AnImplicitFakeAIFallback_OutsideDevelopment_FailsStartup(string environment)
    {
        using var provider = Build([], environment: environment);

        var error = await Should.ThrowAsync<InvalidOperationException>(() => StartAsync(provider));
        error.Message.ShouldContain("AzureOpenAI:Endpoint");
        error.Message.ShouldContain("Nachos:Ai:Mode=Fake");
    }

    [Fact]
    public async Task AnExplicitFakeAI_OutsideDevelopment_StartsWithAWarning()
    {
        var logs = new CapturingProvider();
        using var provider = Build(new() { ["Nachos:Ai:Mode"] = "Fake" }, logs, environment: "Production");

        await StartAsync(provider);

        IsFake(provider).ShouldBeTrue();
        logs.Warnings.Count(w => w.Contains("Fake AI is active", StringComparison.Ordinal)).ShouldBe(1);
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

        provider.GetServices<IHostedService>().Select(h => h.GetType().Name).ShouldNotContain("FakeAiStartupCheck");
    }

    [Fact]
    public async Task AHostWithFakeAI_DoesNotWarnAboutAMissingEmbeddingGenerator()
    {
        var logs = new CapturingProvider();
        using var provider = Build([], logs);

        await StartAsync(provider);

        logs.Warnings.ShouldNotContain(w => w.Contains("No embedding generator", StringComparison.Ordinal));
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "Nachos.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
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
