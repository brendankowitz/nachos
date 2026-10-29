using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Core.Configuration;
using Nachos.Core.Deriver;
using Nachos.Core.Llm;
using Nachos.Core.Worker;
using Nachos.Testing;
using Shouldly;
using static Nachos.Core.Tests.Deriver.DeriverFixture;

namespace Nachos.Core.Tests.Deriver;

public sealed class DeriverTests : IDisposable
{
    private static CancellationToken Ct => CancellationToken.None;

    private readonly DeriverFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static JsonObject Transcript(ChatRequest request) =>
        JsonNode.Parse(request.Messages.Last(m => m.Role == ChatRole.User).Text)!.AsObject();

    [Fact]
    public async Task ExtractsFactsToEveryObserverCollection()
    {
        await _fixture.SeedAsync();
        var message = await _fixture.SayAsync("alice", "I moved to Paris last year.");
        _fixture.Facts(("Alice lives in Paris.", [message.PublicId]));

        await _fixture.HandleAsync(await _fixture.AcquireAsync());

        foreach (var observer in new[] { "alice", "bob" })
        {
            var conclusion = (await _fixture.ConclusionsAsync(observer)).ShouldHaveSingleItem();
            conclusion.Content.ShouldBe("Alice lives in Paris.");
            conclusion.Level.ShouldBe(ConclusionLevel.Explicit);
            conclusion.SessionName.ShouldBe(SessionId);
            conclusion.TimesDerived.ShouldBe(1);
            conclusion.SourceIds.ShouldBeEmpty();
            conclusion.InternalMetadata["source_message_ids"]!.AsArray().Select(n => n!.GetValue<string>())
                .ShouldBe([message.PublicId]);
            conclusion.InternalMetadata["prompt_id"]!.GetValue<string>().ShouldBe("deriver");
            conclusion.InternalMetadata["prompt_version"]!.GetValue<string>().ShouldBe("1");
        }

        var vector = await new DeterministicEmbeddingGenerator().GenerateVectorAsync("Alice lives in Paris.");
        (await _fixture.Store.Conclusions.QueryAsync(WorkspaceId, vector, 10, 0.0001, null, Ct)).Count.ShouldBe(2);
        _fixture.Embeddings.Calls.ShouldBe(1);
        (await _fixture.QueueAsync()).ShouldBe(new QueueCounts(0, 0, 1, 0));
    }

    [Fact]
    public async Task OtherSpeakersAreContextOnly()
    {
        await _fixture.SeedAsync();
        var question = await _fixture.SayAsync("bob", "Do you still live in Lisbon?");
        var answer = await _fixture.SayAsync("alice", "Yes, that's right.");
        _fixture.Facts();

        await _fixture.HandleAsync(await _fixture.AcquireAsync());

        var request = _fixture.Chat.Requests.ShouldHaveSingleItem();
        request.Messages[0].Role.ShouldBe(ChatRole.System);
        request.Messages[0].Text.ShouldContain("alice");
        request.Options!.AdditionalProperties![AgentCallTags.AgentTag].ShouldBe(DeriverService.AgentName);
        request.Options.AdditionalProperties[AgentCallTags.WorkspaceTag].ShouldBe(WorkspaceId);

        var transcript = Transcript(request);
        transcript["target_peer"]!.GetValue<string>().ShouldBe("alice");
        var messages = transcript["messages"]!.AsArray().Select(n => n!.AsObject()).ToList();
        messages.Count.ShouldBe(2);

        messages[0]["speaker"]!.GetValue<string>().ShouldBe("bob");
        messages[0]["role"]!.GetValue<string>().ShouldBe("context");
        messages[0].ContainsKey("id").ShouldBeFalse();
        messages[0]["content"]!.GetValue<string>().ShouldBe(question.Content);

        messages[1]["speaker"]!.GetValue<string>().ShouldBe("alice");
        messages[1]["role"]!.GetValue<string>().ShouldBe("target");
        messages[1]["id"]!.GetValue<string>().ShouldBe(answer.PublicId);
        messages[1]["sent_at"]!.GetValue<string>().ShouldBe("2026-10-29T09:01:00.0000000+00:00");
    }

    private static string Words(string word, int count) => string.Join(' ', Enumerable.Repeat(word, count));

    [Fact]
    public async Task PrecedingContext_StopsAtTheFirstMessageOverMaxInputTokens()
    {
        using var fixture = new DeriverFixture(configureOptions: o => o.Deriver.MaxInputTokens = 2500);
        await fixture.SeedAsync();
        await fixture.SayAsync("bob", "Hello!");
        await fixture.SayAsync("bob", Words("old", 3000));
        var recent = await fixture.SayAsync("bob", "Where do you live?");
        var answer = await fixture.SayAsync("alice", "Paris.");
        fixture.Facts();

        await fixture.HandleAsync(await fixture.AcquireAsync());

        var messages = Transcript(fixture.Chat.Requests.Single())["messages"]!.AsArray().Select(n => n!.AsObject()).ToList();
        messages.Select(m => m["content"]!.GetValue<string>()).ShouldBe([recent.Content, answer.Content]);
    }

    [Fact]
    public async Task BatchMessages_AreKeptEvenOverMaxInputTokens()
    {
        using var fixture = new DeriverFixture(configureOptions: o => o.Deriver.MaxInputTokens = 1000);
        await fixture.SeedAsync();
        await fixture.SayAsync("bob", "Where do you live?");
        var answer = await fixture.SayAsync("alice", "Paris. " + Words("long", 2000));
        fixture.Facts();

        await fixture.HandleAsync(await fixture.AcquireAsync());

        var messages = Transcript(fixture.Chat.Requests.Single())["messages"]!.AsArray();
        messages.ShouldHaveSingleItem()!["id"]!.GetValue<string>().ShouldBe(answer.PublicId);
    }

    [Fact]
    public async Task ZeroFacts_CompletesUnit()
    {
        await _fixture.SeedAsync();
        await _fixture.SayAsync("alice", "Hi there!");
        _fixture.Facts();

        await _fixture.HandleAsync(await _fixture.AcquireAsync());

        (await _fixture.ConclusionsAsync("alice")).ShouldBeEmpty();
        (await _fixture.QueueAsync()).ShouldBe(new QueueCounts(0, 0, 1, 0));
        _fixture.Embeddings.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task InvalidSourceIds_FactDropped()
    {
        await _fixture.SeedAsync();
        var context = await _fixture.SayAsync("bob", "I have a cat.");
        var message = await _fixture.SayAsync("alice", "I have a dog and I play chess.");
        _fixture.Chat.EnqueueJson(new ExtractionResult(
        [
            new ExtractedFact("Alice has a dog.", [message.PublicId]),
            new ExtractedFact("Alice has a cat.", [context.PublicId]),
            new ExtractedFact("Alice has a parrot.", ["no-such-message"]),
            new ExtractedFact("Alice owns a boat.", [message.PublicId, "no-such-message"]),
            new ExtractedFact("Alice plays golf.", []),
            new ExtractedFact("Alice plays tennis.", null),
            new ExtractedFact("   ", [message.PublicId]),
            new ExtractedFact(null, [message.PublicId]),
            new ExtractedFact(" Alice  plays chess. ", [message.PublicId]),
        ]));

        await _fixture.HandleAsync(await _fixture.AcquireAsync());

        (await _fixture.ConclusionsAsync("alice")).Select(c => c.Content)
            .ShouldBe(["Alice has a dog.", "Alice plays chess."], ignoreOrder: true);
        (await _fixture.QueueAsync()).Completed.ShouldBe(1);
    }

    [Fact]
    public async Task NullFacts_IsZeroFacts()
    {
        await _fixture.SeedAsync();
        await _fixture.SayAsync("alice", "Hello.");
        _fixture.Chat.EnqueueText("""{"facts":null}""");

        await _fixture.HandleAsync(await _fixture.AcquireAsync());

        (await _fixture.QueueAsync()).Completed.ShouldBe(1);
    }

    [Fact]
    public async Task RetriedBatch_DoesNotInflateTimesDerived()
    {
        await _fixture.SeedAsync();
        var first = await _fixture.SayAsync("alice", "I like tea.");
        _fixture.Facts(("Alice likes tea.", [first.PublicId]));
        await _fixture.HandleAsync(await _fixture.AcquireAsync());

        var again = await _fixture.SayAsync("alice", "Tea is my favourite drink.");
        _fixture.Facts(("Alice likes tea.", [again.PublicId]));
        _fixture.Embeddings.FailNext = true;
        var crashed = await _fixture.AcquireAsync();
        await Should.ThrowAsync<HttpRequestException>(() => _fixture.HandleAsync(crashed));
        (await _fixture.ConclusionsAsync("alice")).ShouldHaveSingleItem().TimesDerived.ShouldBe(1);
        await _fixture.Store.WorkQueue.FailAsync(crashed, "embedding failed", TimeSpan.Zero, 5, Ct);

        _fixture.Facts(("Alice likes tea.", [again.PublicId]));
        var retry = await _fixture.AcquireAsync();
        retry.Items.Single().Attempts.ShouldBe(2);
        await _fixture.HandleAsync(retry);

        foreach (var observer in new[] { "alice", "bob" })
        {
            (await _fixture.ConclusionsAsync(observer)).ShouldHaveSingleItem().TimesDerived.ShouldBe(2);
        }
    }

    [Fact]
    public async Task UsesEnqueuedObserverSnapshot()
    {
        await _fixture.SeedAsync();
        var message = await _fixture.SayAsync("alice", "I am a nurse.");
        await _fixture.Store.Sessions.AddPeersAsync(
            WorkspaceId, SessionId, new Dictionary<string, SessionPeerConfig> { ["carol"] = new(ObserveOthers: true) }, Ct);
        await _fixture.Store.Sessions.SetPeerConfigAsync(WorkspaceId, SessionId, "bob", new SessionPeerConfig(ObserveOthers: false), Ct);
        _fixture.Facts(("Alice is a nurse.", [message.PublicId]));

        await _fixture.HandleAsync(await _fixture.AcquireAsync());

        (await _fixture.ConclusionsAsync("alice")).ShouldHaveSingleItem();
        (await _fixture.ConclusionsAsync("bob")).ShouldHaveSingleItem();
        (await _fixture.ConclusionsAsync("carol")).ShouldBeEmpty();
        (await _fixture.Store.Conclusions.GetCollectionAsync(WorkspaceId, "carol", "alice", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task FactReachesOnlyObserversOfEverySourceMessage()
    {
        await _fixture.SeedAsync();
        await _fixture.Store.Sessions.SetPeerConfigAsync(WorkspaceId, SessionId, "bob", new SessionPeerConfig(ObserveOthers: false), Ct);
        var unseen = await _fixture.SayAsync("alice", "I was born in Rome.");
        await _fixture.Store.Sessions.SetPeerConfigAsync(WorkspaceId, SessionId, "bob", new SessionPeerConfig(ObserveOthers: true), Ct);
        var seen = await _fixture.SayAsync("alice", "I work as a pilot.");
        _fixture.Facts(
            ("Alice was born in Rome.", [unseen.PublicId]),
            ("Alice is a pilot.", [seen.PublicId]),
            ("Alice is a pilot born in Rome.", [unseen.PublicId, seen.PublicId]));

        var lease = await _fixture.AcquireAsync();
        lease.Items.Count.ShouldBe(2);
        await _fixture.HandleAsync(lease);

        (await _fixture.ConclusionsAsync("alice")).Count.ShouldBe(3);
        (await _fixture.ConclusionsAsync("bob")).ShouldHaveSingleItem().Content.ShouldBe("Alice is a pilot.");
    }

    [Fact]
    public async Task ReasoningDisabledAfterEnqueue_CompletesWithoutLlmCall()
    {
        await _fixture.SeedAsync();
        await _fixture.SayAsync("alice", "I live in Paris.");
        await _fixture.Store.Sessions.UpdateAsync(
            WorkspaceId, SessionId, null, new JsonObject { ["reasoning"] = new JsonObject { ["enabled"] = false } }, Ct);

        await _fixture.HandleAsync(await _fixture.AcquireAsync());

        _fixture.Chat.Requests.ShouldBeEmpty();
        _fixture.Embeddings.Calls.ShouldBe(0);
        (await _fixture.ConclusionsAsync("alice")).ShouldBeEmpty();
        (await _fixture.QueueAsync()).ShouldBe(new QueueCounts(0, 0, 1, 0));
    }

    [Fact]
    public async Task CustomInstructions_AreIncludedInThePrompt()
    {
        await _fixture.SeedAsync();
        await _fixture.Store.Workspaces.UpdateAsync(
            WorkspaceId,
            null,
            new JsonObject
            {
                ["custom_instructions"] = "Prefer metric units.",
                ["reasoning"] = new JsonObject { ["custom_instructions"] = "Ignore food preferences." },
            },
            Ct);
        await _fixture.SayAsync("alice", "I run 5 miles a day.");
        _fixture.Facts();

        await _fixture.HandleAsync(await _fixture.AcquireAsync());

        var system = _fixture.Chat.Requests.Single().Messages[0].Text;
        system.ShouldContain("Prefer metric units.");
        system.ShouldContain("Ignore food preferences.");
    }

    [Fact]
    public async Task LeaseLost_DoesNotComplete()
    {
        await _fixture.SeedAsync();
        var message = await _fixture.SayAsync("alice", "I live in Paris.");
        var stale = await _fixture.AcquireAsync("first");
        _fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        var current = await _fixture.AcquireAsync("second");
        _fixture.Facts(("Alice lives in Paris.", [message.PublicId]));

        await Should.ThrowAsync<LeaseLostException>(() => _fixture.HandleAsync(stale));

        (await _fixture.ConclusionsAsync("alice")).ShouldBeEmpty();
        (await _fixture.QueueAsync()).Completed.ShouldBe(0);

        _fixture.Facts(("Alice lives in Paris.", [message.PublicId]));
        await _fixture.HandleAsync(current);
        (await _fixture.ConclusionsAsync("alice")).ShouldHaveSingleItem().TimesDerived.ShouldBe(1);
        (await _fixture.QueueAsync()).Completed.ShouldBe(1);
    }

    [Fact]
    public async Task RecordsDreamCounters()
    {
        await _fixture.SeedAsync();
        var message = await _fixture.SayAsync("alice", "I have two kids and a dog.");
        _fixture.Facts(("Alice has two kids.", [message.PublicId]), ("Alice has a dog.", [message.PublicId]));

        await _fixture.HandleAsync(await _fixture.AcquireAsync());

        foreach (var observer in new[] { "alice", "bob" })
        {
            var collection = (await _fixture.Store.Conclusions.GetCollectionAsync(WorkspaceId, observer, "alice", Ct)).ShouldNotBeNull();
            collection.NewExplicitCount.ShouldBe(2);
            collection.LastActivityAt.ShouldBe(_fixture.Clock.GetUtcNow());
        }
    }

    [Fact]
    public async Task ModelAnswerThatIsNotJson_FailsTheUnit()
    {
        await _fixture.SeedAsync();
        await _fixture.SayAsync("alice", "I live in Paris.");
        _fixture.Chat.EnqueueText("I could not find any facts.");
        var lease = await _fixture.AcquireAsync();

        await Should.ThrowAsync<InvalidOperationException>(() => _fixture.HandleAsync(lease));

        (await _fixture.QueueAsync()).Completed.ShouldBe(0);
    }

    [Fact]
    public void BatchPolicy_ComesFromDeriverOptions()
    {
        using var fixture = new DeriverFixture(configureOptions: o =>
        {
            o.Deriver.EligibilityTokens = 7;
            o.Deriver.MaxAge = TimeSpan.FromSeconds(9);
            o.Deriver.BatchTokenTarget = 11;
        });

        var handler = fixture.Handler();

        handler.TaskType.ShouldBe(WorkTaskType.Representation);
        handler.BatchPolicy.ShouldBe(new WorkBatchPolicy(7, TimeSpan.FromSeconds(9), 11));
    }
}
