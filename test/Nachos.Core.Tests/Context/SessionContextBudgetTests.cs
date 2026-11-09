using Microsoft.Extensions.Logging;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Core.Context;
using Nachos.Core.Memory;
using Nachos.Core.Tests.Memory;
using Nachos.Core.Tests.Worker;
using Nachos.Core.Tokens;
using Shouldly;
using static Nachos.Core.Tests.Context.ContextKit;

namespace Nachos.Core.Tests.Context;

/// <summary>
/// Review Focus 1: a session context never exceeds its token budget (spec §11.6, content tokens per D5), whatever the
/// sizes of its representation, card, summaries and messages.
/// </summary>
public sealed class SessionContextBudgetTests
{
    private const string W = MemoryFixture.W;
    private const int CasesPerSeed = 50;

    /// <summary>Ten seeds of fifty cases each: 500 random cases.</summary>
    public static TheoryData<int> Seeds => [.. Enumerable.Range(0, 10)];

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task NeverExceedsBudget(int seed)
    {
        using var f = new MemoryFixture(new WordCounter());
        await f.Store.Workspaces.GetOrCreateAsync(W, null, null, default);
        var random = new Random(seed);
        for (var i = 0; i < CasesPerSeed; i++)
        {
            await RunCaseAsync(f, random, $"c{seed}x{i}");
        }

        f.ShouldNotHaveTrippedTheGuard();
    }

    private static async Task RunCaseAsync(MemoryFixture f, Random random, string name)
    {
        var tokens = random.Next(4) switch
        {
            0 => random.Next(1, 11),
            1 => random.Next(1, 201),
            2 => random.Next(1, 2_001),
            _ => random.Next(1, 20_001),
        };
        var scale = Math.Max(1, tokens / random.Next(1, 8));
        int Size() => random.Next(1, 2 * scale + 1);
        string[] Many(int max, string label) => [.. Enumerable.Range(0, random.Next(0, max + 1)).Select(n => Words(Size(), $"{label}{n}"))];

        string session = "S" + name, observer = "o" + name, observed = "t" + name;
        await f.Store.Sessions.GetOrCreateAsync(W, session, null, null,
            new Dictionary<string, SessionPeerConfig> { [observer] = new(), [observed] = new() }, default);
        var covered = Many(3, "covered");
        var hasSummary = covered.Length > 0 && random.Next(3) > 0;
        var messages = await SummarizedSessionAsync(f, session, covered, Many(12, "message"),
            hasSummary && random.Next(2) == 0 ? Words(Size(), "short") : null,
            hasSummary && random.Next(2) == 0 ? Words(Size(), "long") : null,
            peer: observed);

        ContextRepresentation? representation = null;
        if (random.Next(10) < 7)
        {
            foreach (var content in Many(8, "conclusion"))
            {
                await f.AddAsync(content, session, observer, observed);
            }

            if (random.Next(2) == 0)
            {
                await f.PeerContext.SetCardAsync(W, observer, observed, Many(4, "card"), default);
            }

            representation = new(observer, observed, new RepresentationQuery(
                MaxConclusions: random.Next(1, 11),
                IncludeMostFrequent: random.Next(2) == 0,
                SearchQuery: random.Next(2) == 0 ? "conclusion0" : null));
        }

        var request = Request(tokens, summary: random.Next(5) > 0, representation);

        var context = await f.SessionContext.GetContextAsync(W, session, request, VisibilityPolicy.Unrestricted, default);

        ContentTokens(context).ShouldBeLessThanOrEqualTo(tokens, $"case {name}, budget {tokens}");
        var eligible = messages.Where(m => m.Seq > (context.Summary?.CoversThroughSeq ?? 0)).ToArray();
        context.Messages.Select(m => m.Seq).ShouldBe(eligible.TakeLast(context.Messages.Count).Select(m => m.Seq),
            $"case {name}: the newest eligible messages, oldest first");
    }

    [Fact]
    public async Task SingleMessageLargerThanBudget_ReturnsNoMessages()
    {
        using var f = await new MemoryFixture(new WordCounter()).SeedAsync();
        await AppendAsync(f, "S1", [Words(1, "old"), Words(11, "newest")]);

        var context = await f.SessionContext.GetContextAsync(W, "S1", Request(10), VisibilityPolicy.Unrestricted, default);

        // The older message would fit, but the context is the newest stretch of the session: it never skips a message.
        context.Messages.ShouldBeEmpty();
        f.ShouldNotHaveTrippedTheGuard();
    }

    [Fact]
    public async Task TokensOne_FitsOneWordMessageOnly()
    {
        using var f = await new MemoryFixture(new WordCounter()).SeedAsync();
        await AppendAsync(f, "S1", [Words(2, "older"), Words(1, "newest")]);
        await f.AddAsync(Words(1, "fact"));
        await f.PeerContext.SetCardAsync(W, "bob", "alice", [Words(1, "card")], default);

        var context = await f.SessionContext.GetContextAsync(W, "S1", Request(1, representation: Peers()),
            VisibilityPolicy.Unrestricted, default);

        // Half of one token is zero: no card and an empty representation.
        context.PeerCard.ShouldBeNull();
        context.Representation.ShouldBe("");
        context.Messages.Select(m => m.Content).ShouldBe(["newest"]);
    }

    [Fact]
    public async Task RepresentationOver50Percent_Truncated()
    {
        using var f = await new MemoryFixture(new WordCounter()).SeedAsync();
        for (var i = 0; i < 10; i++)
        {
            await f.AddAsync(Words(5, $"fact{i}"));
        }

        await AppendAsync(f, "S1", [Words(30, "message")]);

        // Half of 40 is 20: the "## Explicit" heading (2) and three bullets of 6 (a dash and five words).
        var context = await f.SessionContext.GetContextAsync(W, "S1", Request(40, representation: Peers()),
            VisibilityPolicy.Unrestricted, default);

        context.Representation.ShouldNotBeNull();
        context.Representation.Split('\n').Where(line => line.StartsWith("- ", StringComparison.Ordinal))
            .Select(line => line.Split(' ')[1]).ShouldBe(["fact9", "fact8", "fact7"], ignoreOrder: true);
        new WordCounter().Count(context.Representation).ShouldBe(20);
        context.Messages.ShouldBeEmpty();
        f.ShouldNotHaveTrippedTheGuard();
    }

    [Fact]
    public async Task RepresentationWithinHalf_IsWhole()
    {
        using var f = await new MemoryFixture(new WordCounter()).SeedAsync();
        await f.AddAsync(Words(5, "fact0"));
        await f.AddAsync(Words(5, "fact1"));

        var context = await f.SessionContext.GetContextAsync(W, "S1", Request(28, representation: Peers()),
            VisibilityPolicy.Unrestricted, default);

        context.Representation.ShouldBe(await f.Representations.BuildAsync(W, "bob", "alice", VisibilityPolicy.Unrestricted,
            new RepresentationQuery(), default));
    }

    [Fact]
    public async Task CardOver50Percent_Dropped_RepresentationKept()
    {
        using var f = await new MemoryFixture(new WordCounter()).SeedAsync();
        await f.AddAsync(Words(3, "fact"));
        await f.PeerContext.SetCardAsync(W, "bob", "alice", [Words(6, "card0"), Words(6, "card1")], default);

        var context = await f.SessionContext.GetContextAsync(W, "S1", Request(20, representation: Peers()),
            VisibilityPolicy.Unrestricted, default);

        context.PeerCard.ShouldBeNull();
        context.Representation.ShouldBe("## Explicit\n\n- fact w w\n");
    }

    [Fact]
    public async Task CardAndRepresentation_ShareHalf_CardFirst()
    {
        using var f = await new MemoryFixture(new WordCounter()).SeedAsync();
        await f.AddAsync(Words(3, "fact0"));
        await f.AddAsync(Words(3, "fact1"));
        await f.PeerContext.SetCardAsync(W, "bob", "alice", [Words(4, "card")], default);

        // Half of 20 is 10: the card (4) and a representation of one bullet (2 + 4), not two (2 + 8).
        var context = await f.SessionContext.GetContextAsync(W, "S1", Request(20, representation: Peers()),
            VisibilityPolicy.Unrestricted, default);

        context.PeerCard.ShouldBe([Words(4, "card")]);
        context.Representation.ShouldBe("## Explicit\n\n- fact1 w w\n");
    }

    [Fact]
    public async Task SummaryOver40Percent_Skipped()
    {
        // 40% of 100 is 40: the 41-word long summary does not fit, the 40-word short one does.
        using var f = await new MemoryFixture(new WordCounter()).SeedAsync();
        await SummarizedSessionAsync(f, "S1", [Words(1, "covered")], [Words(1, "after")], Words(40, "short"), Words(41, "long"));

        var context = await f.SessionContext.GetContextAsync(W, "S1", Request(100), VisibilityPolicy.Unrestricted, default);

        context.Summary.ShouldNotBeNull().Kind.ShouldBe(SummaryKind.Short);
        context.Messages.Select(m => m.Content).ShouldBe(["after"]);
    }

    [Fact]
    public async Task BothSummariesOver40Percent_NoSummary_AllMessages()
    {
        using var f = await new MemoryFixture(new WordCounter()).SeedAsync();
        await SummarizedSessionAsync(f, "S1", [Words(1, "covered")], [Words(1, "after")], Words(41, "short"), Words(41, "long"));

        var context = await f.SessionContext.GetContextAsync(W, "S1", Request(100), VisibilityPolicy.Unrestricted, default);

        context.Summary.ShouldBeNull();
        context.Messages.Select(m => m.Content).ShouldBe(["covered", "after"]);
    }

    [Fact]
    public async Task LongSummaryPreferred_WhenItFits()
    {
        using var f = await new MemoryFixture(new WordCounter()).SeedAsync();
        await SummarizedSessionAsync(f, "S1", [Words(1, "covered")], [Words(1, "after")], Words(5, "short"), Words(40, "long"));

        var context = await f.SessionContext.GetContextAsync(W, "S1", Request(100), VisibilityPolicy.Unrestricted, default);

        context.Summary.ShouldNotBeNull().Kind.ShouldBe(SummaryKind.Long);
    }

    [Fact]
    public async Task SummaryShare_IsOfTheRemainderAfterThePeerShare()
    {
        using var f = await new MemoryFixture(new WordCounter()).SeedAsync();
        await f.AddAsync(Words(47, "fact"));
        await SummarizedSessionAsync(f, "S1", [Words(1, "covered")], [], null, Words(21, "long"));

        // The representation takes 50 of 100 (heading 2, bullet 48). 40% of the remaining 50 is 20, so the 21-word
        // summary does not fit, although it is well under 40% of 100.
        var context = await f.SessionContext.GetContextAsync(W, "S1", Request(100, representation: Peers()),
            VisibilityPolicy.Unrestricted, default);

        new WordCounter().Count(context.Representation!).ShouldBe(50);
        context.Summary.ShouldBeNull();
    }

    [Fact]
    public async Task SummaryFalse_NoSummary_MessagesFromTheStart()
    {
        using var f = await new MemoryFixture(new WordCounter()).SeedAsync();
        await SummarizedSessionAsync(f, "S1", [Words(1, "covered")], [Words(1, "after")], Words(1, "short"), null);

        var context = await f.SessionContext.GetContextAsync(W, "S1", Request(100, summary: false),
            VisibilityPolicy.Unrestricted, default);

        context.Summary.ShouldBeNull();
        context.Messages.Select(m => m.Content).ShouldBe(["covered", "after"]);
    }

    [Fact]
    public async Task MessagesChronological()
    {
        using var f = await new MemoryFixture(new WordCounter()).SeedAsync();
        await AppendAsync(f, "S1", [Words(3, "first"), Words(3, "second")]);
        await AppendAsync(f, "S1", [Words(3, "third"), Words(3, "fourth")]);

        var context = await f.SessionContext.GetContextAsync(W, "S1", Request(9), VisibilityPolicy.Unrestricted, default);

        context.Messages.Select(m => m.Content.Split(' ')[0]).ShouldBe(["second", "third", "fourth"]);
    }

    [Fact]
    public async Task ManyMessages_ReadAcrossPages()
    {
        using var f = await new MemoryFixture(new WordCounter()).SeedAsync();
        await AppendAsync(f, "S1", Enumerable.Range(0, 250).Select(i => Words(1, $"m{i}")));

        var context = await f.SessionContext.GetContextAsync(W, "S1", Request(230), VisibilityPolicy.Unrestricted, default);

        context.Messages.Select(m => m.Content).ShouldBe(Enumerable.Range(20, 230).Select(i => $"m{i}"));
    }

    [Fact]
    public async Task FinalGuardNeverTrips()
    {
        using var f = await new MemoryFixture(new WordCounter()).SeedAsync();
        await f.AddAsync(Words(7, "fact"));
        await f.PeerContext.SetCardAsync(W, "bob", "alice", [Words(3, "card")], default);
        await SummarizedSessionAsync(f, "S1", [Words(4, "covered")], [Words(5, "a"), Words(6, "b")], Words(4, "short"), Words(8, "long"));

        foreach (var tokens in Enumerable.Range(1, 80))
        {
            var context = await f.SessionContext.GetContextAsync(W, "S1", Request(tokens, representation: Peers()),
                VisibilityPolicy.Unrestricted, default);
            ContentTokens(context).ShouldBeLessThanOrEqualTo(tokens);
        }

        f.ShouldNotHaveTrippedTheGuard();
    }

    [Fact]
    public async Task FinalGuard_CounterThatDisagreesWithItself_StillWithinBudget_AndLogs()
    {
        using var f = await new MemoryFixture(new WordCounter()).SeedAsync();
        await AppendAsync(f, "S1", [Words(3, "older"), Words(3, "newer")]);
        var logs = new RecordingLogs();
        var drifting = new DriftingCounter();
        var service = new SessionContextService(f.Store, f.PeerContext, drifting, new RecordingLogger<SessionContextService>(logs));

        var context = await service.GetContextAsync(W, "S1", Request(7), VisibilityPolicy.Unrestricted, default);

        // Each message counted 3 while filling, 4 at the final check: 8 > 7, so the guard drops the older one.
        context.Messages.Select(m => m.Content.Split(' ')[0]).ShouldBe(["newer"]);
        logs.At(LogLevel.Error).ShouldHaveSingleItem();
    }

    /// <summary>Counts words, plus one for every time it has seen the text before.</summary>
    private sealed class DriftingCounter : ITokenCounter
    {
        private readonly Dictionary<string, int> _seen = new(StringComparer.Ordinal);

        public int Count(string text)
        {
            var times = _seen.GetValueOrDefault(text);
            _seen[text] = times + 1;
            return new WordCounter().Count(text) + times;
        }
    }
}
