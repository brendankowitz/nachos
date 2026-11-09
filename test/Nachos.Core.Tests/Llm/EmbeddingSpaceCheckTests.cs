using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Core.Llm;
using Nachos.DataLayer.InMemory;
using NSubstitute;
using Shouldly;

namespace Nachos.Core.Tests.Llm;

public sealed class EmbeddingSpaceCheckTests
{
    private const string Fake = DeterministicEmbeddingGenerator.ModelId;
    private const string Azure = "text-embedding-3-small";

    private readonly InMemoryMemoryStore _store = new(TimeProvider.System);

    private readonly StopRecorder _lifetime = new();

    private EmbeddingSpaceCheck Check(
        IEmbeddingGenerator<string, Embedding<float>> generator, ILogger<EmbeddingSpaceCheck>? logger = null, IMemoryStore? store = null)
    {
        var scopes = new ServiceCollection().AddSingleton(store ?? _store).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new(generator, scopes, logger ?? NullLogger<EmbeddingSpaceCheck>.Instance, _lifetime);
    }

    /// <summary>A store whose embedding stamp is unreadable for the first <paramref name="failures"/> reads, then <paramref name="stamp"/>.</summary>
    private static IMemoryStore FlakyStore(int failures, EmbeddingSpace stamp)
    {
        var embeddings = Substitute.For<IEmbeddingStore>();
        var reads = 0;
        embeddings.GetSpaceAsync(Arg.Any<CancellationToken>()).Returns(_ => ++reads <= failures
            ? Task.FromException<EmbeddingSpace?>(new TimeoutException("the database is not up yet"))
            : Task.FromResult<EmbeddingSpace?>(stamp));
        var store = Substitute.For<IMemoryStore>();
        store.Embeddings.Returns(embeddings);
        return store;
    }

    [Fact]
    public async Task StoreUnavailableAtStartup_StartsAndStopsTheHostWhenTheRecheckFindsAMismatch()
    {
        var check = Check(new DeterministicEmbeddingGenerator(), store: FlakyStore(failures: 2, new EmbeddingSpace(Azure, VectorSpace.Dimensions)));

        await check.StartAsync(default);

        await _lifetime.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await check.StopAsync(default);
    }

    [Fact]
    public async Task StoreUnavailable_StopCancelsTheRecheck()
    {
        using var check = Check(new DeterministicEmbeddingGenerator(), store: FlakyStore(failures: int.MaxValue, new EmbeddingSpace(Fake, VectorSpace.Dimensions)));
        await check.StartAsync(default);

        await check.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));

        _lifetime.Stopped.Task.IsCompleted.ShouldBeFalse();
    }

    private async Task<(string Workspace, string Session)> NewSessionAsync()
    {
        await _store.Workspaces.GetOrCreateAsync("w", null, null, default);
        await _store.Sessions.GetOrCreateAsync("w", "s", null, null, null, default);
        await _store.Sessions.AddPeersAsync("w", "s", new Dictionary<string, SessionPeerConfig> { ["alice"] = new(), ["bob"] = new() }, default);
        return ("w", "s");
    }

    private async Task SeedConclusionVectorAsync(string model)
    {
        var (workspace, session) = await NewSessionAsync();
        var vector = new float[VectorSpace.Dimensions];
        vector[0] = 1;
        await _store.Conclusions.InsertAsync(
            workspace,
            [new NewConclusion("alice", "bob", session, ConclusionLevel.Explicit, "bob likes tea", vector, [], new JsonObject { [VectorSpace.ModelMetadataKey] = model })],
            default);
    }

    [Fact]
    public async Task Mismatch_FailsWithResetCommand()
    {
        await _store.Embeddings.StampSpaceAsync(new EmbeddingSpace(Azure, VectorSpace.Dimensions), default);

        var error = await Should.ThrowAsync<InvalidOperationException>(() => Check(new DeterministicEmbeddingGenerator()).StartAsync(default));

        error.Message.ShouldBe(
            $"Embedding model '{Fake}' does not match the database's '{Azure}'. " +
            $"Run 'nachos embeddings reset --model {Fake}' to re-embed, or configure '{Azure}'.");
    }

    [Fact]
    public async Task Unstamped_NoVectors_Stamps()
    {
        var (workspace, session) = await NewSessionAsync();
        await _store.Messages.AppendAsync(workspace, session, new MessageAppend([new NewMessage("bob", "hi", 1, null, null)], Chunks: [["hi"]]), default);

        await Check(new DeterministicEmbeddingGenerator()).StartAsync(default);

        (await _store.Embeddings.GetSpaceAsync(default)).ShouldBe(new EmbeddingSpace(Fake, VectorSpace.Dimensions));
    }

    [Fact]
    public async Task Unstamped_MatchingRecordedModel_Stamps()
    {
        await SeedConclusionVectorAsync(Fake);

        await Check(new DeterministicEmbeddingGenerator()).StartAsync(default);

        (await _store.Embeddings.GetSpaceAsync(default))!.Model.ShouldBe(Fake);
    }

    [Fact]
    public async Task Unstamped_OtherRecordedModel_Fails()
    {
        await SeedConclusionVectorAsync(Azure);

        var error = await Should.ThrowAsync<InvalidOperationException>(() => Check(new DeterministicEmbeddingGenerator()).StartAsync(default));

        error.Message.ShouldBe(EmbeddingSpaceCheck.MismatchMessage(Fake, Azure));
        (await _store.Embeddings.GetSpaceAsync(default)).ShouldBeNull();
    }

    [Fact]
    public async Task Unstamped_ChunkVectors_Fail_BecauseTheyRecordNoModel()
    {
        var (workspace, session) = await NewSessionAsync();
        await _store.Messages.AppendAsync(workspace, session, new MessageAppend([new NewMessage("bob", "hi", 1, null, null)], Chunks: [["hi"]]), default);
        var chunk = (await _store.Embeddings.ListPendingAsync(10, default)).Single();
        var vector = new float[VectorSpace.Dimensions];
        vector[1] = 1;
        await _store.Embeddings.SaveAsync(chunk, vector, Fake, default);

        var error = await Should.ThrowAsync<InvalidOperationException>(() => Check(new DeterministicEmbeddingGenerator()).StartAsync(default));

        error.Message.ShouldBe(EmbeddingSpaceCheck.MismatchMessage(Fake, "(not recorded)"));
    }

    [Fact]
    public async Task Reset_MarksAllPendingAndRestamps()
    {
        await _store.Embeddings.StampSpaceAsync(new EmbeddingSpace(Azure, VectorSpace.Dimensions), default);
        await SeedConclusionVectorAsync(Azure);
        await Should.ThrowAsync<InvalidOperationException>(() => Check(new DeterministicEmbeddingGenerator()).StartAsync(default));

        (await _store.Embeddings.ResetSpaceAsync(EmbeddingSpaceCheck.SpaceOf(Fake), 100, default)).ShouldBe(1);

        await Check(new DeterministicEmbeddingGenerator()).StartAsync(default);
        (await _store.Embeddings.CountPendingAsync(PendingEmbeddingKind.Conclusion, default)).ShouldBe(1);
    }

    [Theory]
    [InlineData("bge-small-en-v1.5", 384)]
    [InlineData("all-minilm-l6-v2", 384)]
    [InlineData(Fake, VectorSpace.Dimensions)]
    [InlineData(Azure, VectorSpace.Dimensions)]
    public void SpaceOf_RecordsTheModelsOwnSize(string model, int dimensions)
    {
        EmbeddingSpaceCheck.SpaceOf(model).ShouldBe(new EmbeddingSpace(model, dimensions));
    }

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

    private sealed class StopRecorder : IHostApplicationLifetime
    {
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => Stopped.TrySetResult();
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

    private sealed class WarningLogger : ILogger<EmbeddingSpaceCheck>
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
