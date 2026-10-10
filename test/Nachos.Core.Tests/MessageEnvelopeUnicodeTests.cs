using System.Text.Json;
using Nachos.Abstractions;
using Shouldly;

namespace Nachos.Core.Tests;

public sealed class MessageEnvelopeUnicodeTests
{
    public static TheoryData<string, string?> MalformedUnicode()
    {
        string[] bodies =
        [
            """{"messages":[{"content":"x","peer_id":"P","metadata":{"\uD800":1}}]}""",
            """{"messages":[{"content":"x","peer_id":"P","metadata":{"x":"\uDC00"}}]}""",
            """{"messages":[{"content":"x","peer_id":"P"}],"\uD800":1}""",
            """{"messages":[{"content":"x","peer_id":"P"}],"extra":"\uD800"}""",
            """{"messages":[{"content":"x","peer_id":"P","extra":[{"name":"\uD800x"}]}]}""",
            """{"messages":[{"content":"x","peer_id":"P","configuration":{"reasoning":{"custom_instructions":"\uD800"}}}]}""",
            """{"messages":[{"content":"\uD800","peer_id":"P"}]}""",
            """{"messages":[{"content":"x","peer_id":"\uD800"}]}""",
            """{"messages":[{"content":"x","peer_id":"P"}],"extra":{"\uDC00":"x"}}""",
        ];
        var cases = new TheoryData<string, string?>();
        foreach (var body in bodies)
        {
            cases.Add(body, null);
            cases.Add(body, "key");
        }
        return cases;
    }

    [Theory]
    [MemberData(nameof(MalformedUnicode))]
    public async Task InvalidUnicode_IsDomainValidationBeforeAnyTokenOrStoreEffect(string body, string? key)
    {
        var counter = new RecordingTokenCounter();
        using var f = new InMemoryServiceFixture(counter);
        await f.Seed();
        await Should.ThrowAsync<NachosValidationException>(() =>
            f.Service.CreateMessagesResponseAsync("W", "S", InMemoryServiceFixture.Json(body), key, default));
        counter.Inputs.ShouldBeEmpty();
        await f.AssertNoMessagesOrPeers(key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("key")]
    public async Task ValidUnicodeEscapesReplacementCharacterAndLiteralBackslashes_ArePreserved(string? key)
    {
        using var f = new InMemoryServiceFixture();
        await f.Seed();
        var body = InMemoryServiceFixture.Json("""
            {"messages":[{"content":"\uD83D\uDE00\uFFFD\\uD800","peer_id":"P","metadata":{"\uD83D\uDE00":"\uFFFD"}}],
             "extra":{"\uD83D\uDE00":["\\uD800","\uD83D\uDE00"]}}
            """);
        var result = await f.Service.CreateMessagesResponseAsync("W", "S", body, key, default);
        result.Status.ShouldBe(201);
        var message = (await f.Service.ListMessagesAsync("W", "S", null, new())).Items.ShouldHaveSingleItem();
        message.Content.ShouldBe("\U0001F600\uFFFD\\uD800");
        message.Metadata["\U0001F600"]!.GetValue<string>().ShouldBe("\uFFFD");
    }

    [Fact]
    public async Task DisposedEnvelope_RemainsAProgrammingError()
    {
        using var f = new InMemoryServiceFixture();
        await f.Seed();
        var document = JsonDocument.Parse("""{"messages":[{"content":"x","peer_id":"P"}]}""");
        var element = document.RootElement;
        document.Dispose();
        await Should.ThrowAsync<ObjectDisposedException>(() =>
            f.Service.CreateMessagesResponseAsync("W", "S", element, "key", default));
        await f.AssertNoMessagesOrPeers("key");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("key")]
    public async Task IndependentCounterFailure_RetainsIdentityAndLeavesNoMutations(string? key)
    {
        var failure = new InvalidOperationException("independent tokenizer failure");
        var counter = new RecordingTokenCounter { Failure = failure };
        using var f = new InMemoryServiceFixture(counter);
        await f.Seed();
        (await Should.ThrowAsync<InvalidOperationException>(() =>
            f.Service.CreateMessagesResponseAsync("W", "S",
                InMemoryServiceFixture.Json("""{"messages":[{"content":"x","peer_id":"P"}]}"""), key, default)))
            .ShouldBeSameAs(failure);
        await f.AssertNoMessagesOrPeers(key);
    }
}
