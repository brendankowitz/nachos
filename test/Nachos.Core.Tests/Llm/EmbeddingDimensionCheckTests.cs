using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nachos.Core.Llm;
using Shouldly;

namespace Nachos.Core.Tests.Llm;

public sealed class EmbeddingDimensionCheckTests
{
    private static EmbeddingDimensionCheck Check(IEmbeddingGenerator<string, Embedding<float>> generator, ILogger<EmbeddingDimensionCheck>? logger = null) =>
        new(generator, logger ?? NullLogger<EmbeddingDimensionCheck>.Instance);

    [Fact]
    public async Task Mismatch_FailsStartupWithActionableMessage()
    {
        var check = Check(new DeterministicEmbeddingGenerator(dimensions: 768));

        var error = await Should.ThrowAsync<InvalidOperationException>(
            () => check.StartAsync(CancellationToken.None));

        error.Message.ShouldContain("768");
        error.Message.ShouldContain("1536");
        error.Message.ShouldContain("Embeddings:RequestDimensions");
    }

    [Fact]
    public async Task NoEmbeddingForTheProbe_FailsStartupWithActionableMessage()
    {
        var check = Check(new EmptyGenerator());

        var error = await Should.ThrowAsync<InvalidOperationException>(() => check.StartAsync(CancellationToken.None));

        error.Message.ShouldContain("returned 0 embeddings");
    }

    [Fact]
    public async Task MatchingDimensions_StartsNormally()
    {
        await Check(new DeterministicEmbeddingGenerator()).StartAsync(CancellationToken.None);
    }

    public static TheoryData<string, Exception> PassingFailures => new()
    {
        { "connection error", new HttpRequestException("refused") },
        { "503", new HttpRequestException("down", null, HttpStatusCode.ServiceUnavailable) },
        { "403 while a role propagates", new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden) },
        { "timeout", new TimeoutException() },
    };

    [Theory]
    [MemberData(nameof(PassingFailures))]
    public async Task TransientOrAuthorizationProbeFailure_LogsAWarning_AndStartupContinues(string name, Exception failure)
    {
        var logger = new WarningLogger();
        var check = Check(new ThrowingGenerator(failure), logger);

        await check.StartAsync(CancellationToken.None);

        logger.Warnings.ShouldBe(1, name);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task RefusedProbe_FailsStartup(HttpStatusCode status)
    {
        var refusal = new HttpRequestException("refused", null, status);
        var check = Check(new ThrowingGenerator(refusal));

        var error = await Should.ThrowAsync<InvalidOperationException>(() => check.StartAsync(CancellationToken.None));

        error.InnerException.ShouldBeSameAs(refusal);
        error.Message.ShouldContain("Embeddings settings");
    }

    [Fact]
    public async Task CancelledStartup_StillPropagates()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var check = Check(new ThrowingGenerator(new OperationCanceledException(cancelled.Token)));

        await Should.ThrowAsync<OperationCanceledException>(() => check.StartAsync(cancelled.Token));
    }

    private sealed class ThrowingGenerator(Exception failure) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromException<GeneratedEmbeddings<Embedding<float>>>(failure);

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class EmptyGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GeneratedEmbeddings<Embedding<float>>());

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class WarningLogger : ILogger<EmbeddingDimensionCheck>
    {
        public int Warnings { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings++;
            }
        }
    }
}
