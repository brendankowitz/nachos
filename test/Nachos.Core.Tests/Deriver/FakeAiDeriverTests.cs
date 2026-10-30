using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Nachos.Core.Llm;
using Shouldly;

namespace Nachos.Core.Tests.Deriver;

public sealed class FakeAiDeriverTests
{
    [Fact]
    public async Task UseFakeAI_DerivesOneConclusionPerTargetMessage()
    {
        using var fixture = new DeriverFixture(configureAi: builder => builder.UseFakeAI());
        await fixture.SeedAsync();
        var first = await fixture.SayAsync("alice", "I live in Paris.");
        await fixture.SayAsync("bob", "Nice! I live in Rome.");
        var second = await fixture.SayAsync("alice", "I have a dog.");

        await fixture.HandleAsync(await fixture.AcquireAsync());

        foreach (var observer in new[] { "alice", "bob" })
        {
            var conclusions = await fixture.ConclusionsAsync(observer);
            conclusions.Select(c => c.Content).ShouldBe([first.Content, second.Content], ignoreOrder: true);
            conclusions.Single(c => c.Content == first.Content).InternalMetadata["source_message_ids"]!.AsArray()
                .Select(n => n!.GetValue<string>()).ShouldBe([first.PublicId]);
        }

        (await fixture.ConclusionsAsync("bob", observed: "bob")).ShouldBeEmpty();
        (await fixture.QueueAsync()).Completed.ShouldBe(2);
    }

    [Fact]
    public async Task UseFakeAI_RegistersTheDeterministicEmbeddingGenerator_AndRejectsOtherAgents()
    {
        using var fixture = new DeriverFixture(configureAi: builder => builder.UseFakeAI());

        fixture.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>()
            .GetService<DeterministicEmbeddingGenerator>().ShouldNotBeNull();
        var chat = fixture.Services.GetRequiredService<NachosChatClients>().ForAgent("dialectic");
        var error = await Should.ThrowAsync<NotSupportedException>(
            () => chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], AgentCallTags.CreateOptions("dialectic", "W")));
        error.Message.ShouldContain("UseFakeAI");
    }
}
