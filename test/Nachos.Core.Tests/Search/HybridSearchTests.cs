using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Core.Llm;
using Nachos.Core.Memory;
using Nachos.Core.Tests.Deriver;
using Nachos.Core.Tests.Memory;
using Nachos.DataLayer.InMemory;
using Shouldly;

namespace Nachos.Core.Tests.Search;

public sealed class HybridSearchTests
{
    private const string W = MemoryFixture.W;

    private static async Task<string[]> SessionSearchAsync(MemoryFixture f, string query, int limit = 10, string session = "S1") =>
        [.. (await f.Service.SearchSessionAsync(W, session, new MessageSearchOptions(query, limit))).Select(m => m.Content)];

    private static async Task<ValidationError> RejectedAsync(Func<Task> call) =>
        (await Should.ThrowAsync<RequestValidationException>(call)).Errors.ShouldHaveSingleItem();

    [Fact]
    public async Task FusesBothLists()
    {
        using var f = await new MemoryFixture().SeedAsync();
        f.Embeddings.Vectors["citrus"] = FlakyEmbeddingGenerator.Axis(0);
        await f.PostAsync("alice", "lemons are sour", vector: FlakyEmbeddingGenerator.Axis(0));
        await f.PostAsync("alice", "a citrus grove", vector: FlakyEmbeddingGenerator.Axis(0, tilt: 0.5f));
        await f.PostAsync("alice", "unrelated", vector: FlakyEmbeddingGenerator.Axis(9));

        // Vector alone ranks "lemons" first and lexical alone finds only the grove; fused, the grove (1/62 + 1/61)
        // beats "lemons" (1/61), and the limit cuts the third.
        (await SessionSearchAsync(f, "citrus", limit: 2)).ShouldBe(["a citrus grove", "lemons are sour"]);
        (await SessionSearchAsync(f, "citrus")).ShouldBe(["a citrus grove", "lemons are sour", "unrelated"]);
    }

    [Fact]
    public async Task PendingEmbedding_StillFoundLexically()
    {
        using var f = await new MemoryFixture().SeedAsync();
        f.Embeddings.Vectors["kiwi"] = FlakyEmbeddingGenerator.Axis(0);
        await f.PostAsync("alice", "kiwi smoothie, chunk not embedded yet");
        await f.Store.Messages.AppendAsync(W, "S1",
            new MessageAppend([new NewMessage("alice", "kiwi from before M3, never chunked", 5, null, f.Clock.GetUtcNow())]), default);
        await f.PostAsync("alice", "embedded but unrelated", vector: FlakyEmbeddingGenerator.Axis(0));

        var found = await SessionSearchAsync(f, "kiwi");

        found.ShouldBe(["kiwi smoothie, chunk not embedded yet", "kiwi from before M3, never chunked", "embedded but unrelated"],
            ignoreOrder: true);
    }

    [Fact]
    public async Task NoEmbeddingGenerator_LexicalOnly()
    {
        using var f = new InMemoryServiceFixture();
        await f.Seed();
        await f.Service.CreateMessagesAsync("W", "S", [new MessageCreate("green tea", "alice"), new MessageCreate("black coffee", "alice")]);

        var found = await f.Service.SearchSessionAsync("W", "S", new MessageSearchOptions("TEA"));
        var symbolsOnly = await f.Service.SearchWorkspaceAsync("W", new WorkspaceMessageSearchOptions("\"*\" ~"));

        found.Select(m => m.Content).ShouldBe(["green tea"]);
        symbolsOnly.ShouldBeEmpty();
    }

    [Fact]
    public async Task EmbeddingOutage_IsServiceUnavailable()
    {
        using var f = await new MemoryFixture().SeedAsync();
        await f.PostAsync("alice", "tea", vector: FlakyEmbeddingGenerator.Axis(0));
        f.Embeddings.FailNext = true;

        var outage = await Should.ThrowAsync<ServiceUnavailableException>(() => SessionSearchAsync(f, "tea"));

        outage.RetryAfter.ShouldNotBeNull();
    }

    [Fact]
    public async Task BlankQuery_ReturnsEmpty_WithoutEmbedding()
    {
        using var f = await new MemoryFixture().SeedAsync();
        await f.PostAsync("alice", "tea", vector: FlakyEmbeddingGenerator.Axis(0));

        (await SessionSearchAsync(f, "")).ShouldBeEmpty();
        (await SessionSearchAsync(f, " \t\n")).ShouldBeEmpty();
        f.Embeddings.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Filters_NarrowTheCandidates()
    {
        using var f = await new MemoryFixture().SeedAsync();
        await f.PostAsync("alice", "alice drinks tea", vector: FlakyEmbeddingGenerator.Axis(0));
        await f.PostAsync("bob", "bob drinks tea", vector: FlakyEmbeddingGenerator.Axis(0));

        var found = await f.Service.SearchWorkspaceAsync(W,
            new WorkspaceMessageSearchOptions("tea", Filters: new JsonObject { ["peer_id"] = "bob" }));

        found.Select(m => m.Content).ShouldBe(["bob drinks tea"]);
    }

    [Theory]
    [InlineData(0, "greater_than_equal")]
    [InlineData(101, "less_than_equal")]
    public async Task LimitBounds_422(int limit, string type)
    {
        using var f = await new MemoryFixture().SeedAsync();

        foreach (var call in new Func<Task>[]
        {
            () => f.Service.SearchWorkspaceAsync(W, new WorkspaceMessageSearchOptions("tea", limit)),
            () => f.Service.SearchSessionAsync(W, "S1", new MessageSearchOptions("tea", limit)),
            () => f.Service.SearchPeerAsync(W, "alice", new MessageSearchOptions("tea", limit)),
        })
        {
            var error = await RejectedAsync(call);
            error.Loc.ShouldBe(["body", "limit"]);
            error.Type.ShouldBe(type);
        }

        (await f.Service.SearchSessionAsync(W, "S1", new MessageSearchOptions("tea", 1))).ShouldBeEmpty();
        (await f.Service.SearchSessionAsync(W, "S1", new MessageSearchOptions("tea", 100))).ShouldBeEmpty();
        new MessageSearchOptions("tea").Limit.ShouldBe(10);
    }

    [Fact]
    public async Task MissingQuery_422()
    {
        using var f = await new MemoryFixture().SeedAsync();

        var error = await RejectedAsync(() => f.Service.SearchSessionAsync(W, "S1", new MessageSearchOptions(null!)));

        error.Loc.ShouldBe(["body", "query"]);
        error.Type.ShouldBe("string_type");
    }

    [Theory]
    [InlineData("")]
    [InlineData("team-a")]
    public async Task WorkspaceScope_422(string scope)
    {
        using var f = await new MemoryFixture().SeedAsync();

        var rejected = await Should.ThrowAsync<NachosValidationException>(
            () => f.Service.SearchWorkspaceAsync(W, new WorkspaceMessageSearchOptions("tea", Scope: scope)));

        rejected.Detail.ShouldBe(VisibilityPolicy.ScopeNotSupported);
        f.Embeddings.Calls.ShouldBe(0);
    }

    [Fact]
    public void InvalidRequests_FaultTheTask_RatherThanThrowing()
    {
        using var f = new MemoryFixture();

        f.Service.SearchWorkspaceAsync(W, new WorkspaceMessageSearchOptions("tea", Scope: "s")).IsFaulted.ShouldBeTrue();
        f.Service.SearchSessionAsync(W, "S1", new MessageSearchOptions("tea", 0)).IsFaulted.ShouldBeTrue();
        f.Service.SearchPeerAsync(W, "bad id!", new MessageSearchOptions("tea")).IsFaulted.ShouldBeTrue();
    }

    [Fact]
    public async Task LongQuery_EmbedsOnlyItsFirstChunk()
    {
        var generator = new TokenLimitedEmbeddingGenerator(EmbeddingChunking.DefaultMaxTokens);
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddNachos(builder => builder.UseInMemory()
            .UseEmbeddingGenerator(_ => generator, _ => EmbeddingChunking.Cl100kBase()));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<NachosService>();
        await service.GetOrCreateWorkspaceAsync(W);
        await service.GetOrCreateSessionAsync(W, "S1");
        await service.CreateMessagesAsync(W, "S1", [new MessageCreate("I like lemons", "alice")]);
        var query = string.Join(' ', Enumerable.Repeat("citrus", 5000)) + " lemons";

        var found = await service.SearchSessionAsync(W, "S1", new MessageSearchOptions(query));

        found.Select(m => m.Content).ShouldBe(["I like lemons"]);
        generator.Inputs.ShouldContain(input => input.StartsWith("citrus", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostileQuery_ReturnsNormalResults()
    {
        using var f = await new MemoryFixture().SeedAsync();
        await f.PostAsync("alice", "lemon tart near the pie", vector: FlakyEmbeddingGenerator.Axis(0));
        var tooLong = new string('x', 5000);

        foreach (var query in new[] { "\"lemon\" AND (tart* OR NEAR~pie)", "*", "\"", "( ~ )", tooLong, $"{tooLong} lemon" })
        {
            await Should.NotThrowAsync(() => SessionSearchAsync(f, query));
        }

        (await SessionSearchAsync(f, $"{tooLong} lemon")).ShouldContain("lemon tart near the pie");
    }
}
