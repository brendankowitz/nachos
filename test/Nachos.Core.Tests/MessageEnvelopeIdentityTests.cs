using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Shouldly;

namespace Nachos.Core.Tests;

public sealed class MessageEnvelopeIdentityTests
{
    [Theory]
    [InlineData("1", "1.0")]
    [InlineData("1e400", "1e401")]
    [InlineData("12345678901234567890123456789012345678901234567890", "12345678901234567890123456789012345678901234567891")]
    public async Task LargeNumbersAndNumericLexemes_RemainOriginalRequestIdentity(string firstNumber, string secondNumber)
    {
        using var f = new InMemoryServiceFixture();
        await f.Seed();
        var body = """{"messages":[{"content":"x","peer_id":"P","metadata":{"n":NUMBER}}],"extra":NUMBER}"""
            .Replace("NUMBER", firstNumber, StringComparison.Ordinal);
        var first = await f.Service.CreateMessagesResponseAsync("W", "S", InMemoryServiceFixture.Json(body), "key", default);
        (await f.Service.CreateMessagesResponseAsync("W", "S", InMemoryServiceFixture.Json(body), "key", default)).ShouldBe(first);
        var message = (await f.Service.ListMessagesAsync("W", "S", null, new())).Items.ShouldHaveSingleItem();
        message.Metadata["n"]!.ToJsonString().ShouldBe(firstNumber);
        await Should.ThrowAsync<IdempotencyKeyReusedException>(() =>
            f.Service.CreateMessagesResponseAsync("W", "S",
                InMemoryServiceFixture.Json(body.Replace(firstNumber, secondNumber, StringComparison.Ordinal)), "key", default));
    }

    [Theory]
    [InlineData("1e400")]
    [InlineData("12345678901234567890123456789012345678901234567890")]
    public async Task LargeValidNumbers_AreAlsoAcceptedWithoutAKey(string number)
    {
        using var f = new InMemoryServiceFixture();
        await f.Seed();
        var body = """{"messages":[{"content":"x","peer_id":"P","metadata":{"n":NUMBER}}],"extra":NUMBER}"""
            .Replace("NUMBER", number, StringComparison.Ordinal);
        (await f.Service.CreateMessagesResponseAsync("W", "S", InMemoryServiceFixture.Json(body), null, default)).Status.ShouldBe(201);
    }

    [Theory]
    [InlineData("""{"messages":[{"content":"x","peer_id":"P"}],"extra":1,"extra":2}""",
        """{"messages":[{"content":"x","peer_id":"P"}],"extra":2}""")]
    [InlineData("""{"messages":[{"content":"earlier","content":"x","peer_id":"P"}]}""",
        """{"messages":[{"content":"x","peer_id":"P"}]}""")]
    [InlineData("""{"messages":[{"content":null,"content":"x","peer_id":"P"}]}""",
        """{"messages":[{"content":"x","peer_id":"P"}]}""")]
    [InlineData("""{"messages":[{"content":"x","peer_id":null,"peer_id":"P"}]}""",
        """{"messages":[{"content":"x","peer_id":"P"}]}""")]
    [InlineData("""{"messages":[{"content":"x","peer_id":"P"}],"extra":[1,2]}""",
        """{"messages":[{"content":"x","peer_id":"P"}],"extra":[2,1]}""")]
    public async Task AcceptedDuplicatesUnknownFieldsAndArrayOrder_AreNotRewritten(string body, string changed)
    {
        using var f = new InMemoryServiceFixture();
        await f.Seed();
        var first = await f.Service.CreateMessagesResponseAsync("W", "S", InMemoryServiceFixture.Json(body), "key", default);
        (await f.Service.CreateMessagesResponseAsync("W", "S", InMemoryServiceFixture.Json(body), "key", default)).ShouldBe(first);
        (await f.Service.ListMessagesAsync("W", "S", null, new())).Items.ShouldHaveSingleItem().Content.ShouldBe("x");
        await Should.ThrowAsync<IdempotencyKeyReusedException>(() =>
            f.Service.CreateMessagesResponseAsync("W", "S", InMemoryServiceFixture.Json(changed), "key", default));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("key")]
    public async Task DuplicateMetadataNames_KeepTheirExistingRejection(string? key)
    {
        using var f = new InMemoryServiceFixture();
        await f.Seed();
        await Should.ThrowAsync<NachosValidationException>(() => f.Service.CreateMessagesResponseAsync("W", "S",
            InMemoryServiceFixture.Json("""{"messages":[{"content":"x","peer_id":"P","metadata":{"x":1,"x":2}}]}"""), key, default));
        await f.AssertNoMessagesOrPeers(key);
    }

    [Theory]
    [InlineData(64, null)]
    [InlineData(64, "key")]
    [InlineData(65, null)]
    [InlineData(65, "key")]
    public async Task RawMetadata_RetainsItsIndependent64ContainerAllowance(int depth, string? key)
    {
        using var f = new InMemoryServiceFixture();
        await f.Seed();
        var metadata = new JsonObject { ["text"] = "valid" };
        for (var i = 1; i < depth; i++) metadata = new JsonObject { ["next"] = metadata };
        var body = InMemoryServiceFixture.Json(
            """{"messages":[{"content":"x","peer_id":"P","metadata":""" + metadata.ToJsonString() + "}]}", 100);
        if (depth == 65)
        {
            await Should.ThrowAsync<NachosValidationException>(() =>
                f.Service.CreateMessagesResponseAsync("W", "S", body, key, default));
            await f.AssertNoMessagesOrPeers(key);
        }
        else
        {
            var first = await f.Service.CreateMessagesResponseAsync("W", "S", body, key, default);
            first.Status.ShouldBe(201);
            JsonNode.DeepEquals((await f.Service.ListMessagesAsync("W", "S", null, new())).Items[0].Metadata, metadata).ShouldBeTrue();
            if (key is not null)
                (await f.Service.CreateMessagesResponseAsync("W", "S", body, key, default)).ShouldBe(first);
        }
    }

    [Fact]
    public async Task UnknownEnvelopeContainers_DoNotAcquireTheMetadataDepthCap()
    {
        using var f = new InMemoryServiceFixture();
        await f.Seed();
        var nested = new string('[', 100) + "\"valid\"" + new string(']', 100);
        var body = InMemoryServiceFixture.Json(
            """{"messages":[{"content":"x","peer_id":"P"}],"extra":""" + nested + "}", 128);
        (await f.Service.CreateMessagesResponseAsync("W", "S", body, "key", default)).Status.ShouldBe(201);
    }
}
