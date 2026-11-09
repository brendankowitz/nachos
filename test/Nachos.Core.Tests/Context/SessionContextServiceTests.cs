using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Core.Llm;
using Nachos.Core.Tests.Memory;
using Nachos.Core.Tests.Search;
using Shouldly;
using static Nachos.Core.Tests.Context.ContextKit;

namespace Nachos.Core.Tests.Context;

/// <summary><c>GET S/context</c> and <c>GET S/summaries</c> through <see cref="NachosService"/>.</summary>
public sealed class SessionContextServiceTests
{
    private const string W = MemoryFixture.W;

    private static async Task<MemoryFixture> SeededAsync()
    {
        var f = await new MemoryFixture(new WordCounter()).SeedAsync();
        await f.AddAsync("bob thinks alice likes tea", session: "S1");
        await f.AddAsync("bob thinks alice likes S2 tea", session: "S2");
        await f.AddAsync("alice thinks alice likes coffee", session: "S1", observer: "alice");
        await f.PeerContext.SetCardAsync(W, "bob", "alice", ["IDENTITY: alice"], default);
        await f.PeerContext.SetCardAsync(W, "alice", "alice", ["IDENTITY: me"], default);
        await AppendAsync(f, "S1", ["hello", "there"]);
        return f;
    }

    [Fact]
    public async Task NoPeerTarget_NoRepresentationOrCard()
    {
        using var f = await SeededAsync();

        var context = await f.Service.GetSessionContextAsync(W, "S1", new SessionContextQuery());

        context.Id.ShouldBe("S1");
        context.PeerRepresentation.ShouldBeNull();
        context.PeerCard.ShouldBeNull();
        context.Summary.ShouldBeNull();
        context.Messages.Select(m => m.Content).ShouldBe(["hello", "there"]);
    }

    [Fact]
    public async Task PeerTargetAlone_IsItsOwnObserver()
    {
        using var f = await SeededAsync();

        var context = await f.Service.GetSessionContextAsync(W, "S1", new SessionContextQuery(PeerTarget: "alice"));

        context.PeerRepresentation.ShouldBe("## Explicit\n\n- alice thinks alice likes coffee\n");
        context.PeerCard.ShouldBe(["IDENTITY: me"]);
    }

    [Fact]
    public async Task PeerPerspective_IsObserver()
    {
        using var f = await SeededAsync();

        var context = await f.Service.GetSessionContextAsync(W, "S1",
            new SessionContextQuery(PeerTarget: "alice", PeerPerspective: "bob"));

        context.PeerRepresentation.ShouldNotBeNull().ShouldContain("bob thinks alice likes tea");
        context.PeerRepresentation.ShouldContain("bob thinks alice likes S2 tea");
        context.PeerRepresentation.ShouldNotContain("coffee");
        context.PeerCard.ShouldBe(["IDENTITY: alice"]);
    }

    [Fact]
    public async Task LimitToSession_OmitsCard()
    {
        using var f = await SeededAsync();

        var context = await f.Service.GetSessionContextAsync(W, "S1",
            new SessionContextQuery(PeerTarget: "alice", PeerPerspective: "bob", LimitToSession: true));

        context.PeerRepresentation.ShouldBe("## Explicit\n\n- bob thinks alice likes tea\n");
        context.PeerCard.ShouldBeNull();
        context.Messages.Select(m => m.Content).ShouldBe(["hello", "there"]);
    }

    [Fact]
    public async Task Sessions_AddToTheAllowlist()
    {
        using var f = await SeededAsync();

        var context = await f.Service.GetSessionContextAsync(W, "S1",
            new SessionContextQuery(PeerTarget: "alice", PeerPerspective: "bob", LimitToSession: true, Sessions: ["S2"]));

        context.PeerRepresentation.ShouldNotBeNull().ShouldContain("likes tea");
        context.PeerRepresentation.ShouldContain("likes S2 tea");
        context.PeerCard.ShouldBeNull();
        context.Messages.Count.ShouldBe(2);
    }

    [Fact]
    public async Task MessagesStartAfterSummaryCoverage()
    {
        using var f = await new MemoryFixture(new WordCounter()).SeedAsync();
        var messages = await SummarizedSessionAsync(f, "S1", ["one", "two"], ["three", "four"], "short summary", null);

        var context = await f.Service.GetSessionContextAsync(W, "S1", new SessionContextQuery());

        context.Messages.Select(m => m.Content).ShouldBe(["three", "four"]);
        var summary = context.Summary.ShouldNotBeNull();
        summary.Content.ShouldBe("short summary");
        summary.SummaryType.ShouldBe("short");
        summary.MessageId.ShouldBe(messages[1].PublicId);
        summary.TokenCount.ShouldBe(2);
        summary.CreatedAt.ShouldBe((await f.Store.Summaries.GetAsync(W, "S1", SummaryKind.Short, default))!.CreatedAt);
    }

    [Fact]
    public async Task Summaries_ReturnsBothKinds()
    {
        using var f = await new MemoryFixture(new WordCounter()).SeedAsync();
        var messages = await SummarizedSessionAsync(f, "S1", ["one", "two"], ["three"], "short one", "long one here");

        var summaries = await f.Service.GetSessionSummariesAsync(W, "S1");

        summaries.Id.ShouldBe("S1");
        summaries.ShortSummary.ShouldNotBeNull().ShouldSatisfyAllConditions(
            s => s.Content.ShouldBe("short one"),
            s => s.SummaryType.ShouldBe("short"),
            s => s.MessageId.ShouldBe(messages[1].PublicId));
        summaries.LongSummary.ShouldNotBeNull().ShouldSatisfyAllConditions(
            s => s.Content.ShouldBe("long one here"),
            s => s.SummaryType.ShouldBe("long"),
            s => s.TokenCount.ShouldBe(3));
    }

    [Fact]
    public async Task Summaries_NoneYet_BothNull()
    {
        using var f = await new MemoryFixture().SeedAsync();

        var summaries = await f.Service.GetSessionSummariesAsync(W, "S1");

        summaries.ShouldBe(new SessionSummaries("S1", null, null));
    }

    [Fact]
    public async Task MissingSession_NotFound()
    {
        using var f = await new MemoryFixture().SeedAsync();

        await Should.ThrowAsync<NotFoundException>(() => f.Service.GetSessionContextAsync(W, "nope", new SessionContextQuery()));
        await Should.ThrowAsync<NotFoundException>(() => f.Service.GetSessionSummariesAsync(W, "nope"));
        await Should.ThrowAsync<NotFoundException>(() => f.Service.GetSessionContextAsync("nope", "S1", new SessionContextQuery()));
    }

    [Fact]
    public void InvalidRequests_FaultTheTask_RatherThanThrowing()
    {
        using var f = new MemoryFixture();

        f.Service.GetSessionContextAsync(W, "S1", new SessionContextQuery(Tokens: 0)).IsFaulted.ShouldBeTrue();
        f.Service.GetSessionContextAsync(W, "S1", new SessionContextQuery(PeerPerspective: "bob")).IsFaulted.ShouldBeTrue();
        f.Service.GetSessionContextAsync(W, "S1", new SessionContextQuery(PeerTarget: "a", Scope: "s")).IsFaulted.ShouldBeTrue();
        f.Service.GetSessionContextAsync(W, "S1", null!).IsFaulted.ShouldBeTrue();
        f.Service.GetSessionSummariesAsync(W, "bad id!").IsFaulted.ShouldBeTrue();
    }

    [Fact]
    public async Task LongSearchQuery_EmbedsOnlyItsFirstChunk()
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

        var context = await service.GetSessionContextAsync(W, "S1", new SessionContextQuery(PeerTarget: "alice", SearchQuery: query));

        context.Messages.Select(m => m.Content).ShouldBe(["I like lemons"]);
        generator.Inputs.ShouldContain(input => input.StartsWith("citrus", StringComparison.Ordinal));
    }
}
