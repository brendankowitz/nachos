using Nachos.Abstractions;
using Shouldly;

namespace Nachos.Core.Tests;

public sealed class MessageEnvelopeSchemaTests
{
    public static TheoryData<string, string, string, string?> InvalidMessages()
    {
        (string Json, string Field, string Code)[] invalid =
        [
            ("""{"peer_id":"P"}""", "content", "missing"),
            ("""{"content":"x"}""", "peer_id", "missing"),
            ("""{"content":null,"peer_id":"P"}""", "content", "string_type"),
            ("""{"content":"x","peer_id":null}""", "peer_id", "string_type"),
            ("""{"content":12,"peer_id":"P"}""", "content", "string_type"),
            ("""{"content":"x","peer_id":12}""", "peer_id", "string_type"),
            ("""{"content":"x","peer_id":"P","metadata":[]}""", "metadata", "dict_type"),
            ("""{"content":"x","peer_id":"P","configuration":true}""", "configuration", "model_type"),
            ("""{"content":"x","peer_id":"P","configuration":{"reasoning":[]}}""", "configuration.reasoning", "model_type"),
            ("""{"content":"x","peer_id":"P","configuration":{"reasoning":{"enabled":"yes"}}}""", "configuration.reasoning.enabled", "bool_type"),
            ("""{"content":"x","peer_id":"P","configuration":{"reasoning":{"custom_instructions":12}}}""", "configuration.reasoning.custom_instructions", "string_type"),
            ("""{"content":"x","peer_id":"P","created_at":12}""", "created_at", "string_type"),
            ("""{"content":"x","peer_id":"P","created_at":"not-a-date"}""", "created_at", "datetime_parsing"),
        ];
        var cases = new TheoryData<string, string, string, string?>();
        foreach (var item in invalid)
        {
            cases.Add(item.Json, item.Field, item.Code, null);
            cases.Add(item.Json, item.Field, item.Code, "key");
        }
        return cases;
    }

    [Theory]
    [MemberData(nameof(InvalidMessages))]
    public async Task RawSchemaFailure_HasExactSecondMessageLocationBeforeAnyTokenOrWrite(
        string invalid, string field, string code, string? key)
    {
        var counter = new RecordingTokenCounter();
        using var f = new InMemoryServiceFixture(counter);
        await f.Seed();
        var body = InMemoryServiceFixture.Json(
            """{"messages":[{"content":"valid","peer_id":"first"},""" + invalid + "]}");
        var error = await Should.ThrowAsync<RequestValidationException>(() =>
            f.Service.CreateMessagesResponseAsync("W", "S", body, key, default));
        var detail = error.Errors.ShouldHaveSingleItem();
        detail.Loc.ShouldBe(new object[] { "body", "messages", 1 }.Concat(field.Split('.')));
        detail.Type.ShouldBe(code);
        counter.Inputs.ShouldBeEmpty();
        await f.AssertNoMessagesOrPeers(key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad id")]
    public async Task TypedInvalidPeerIds_KeepDomainErrors(string? peerId)
    {
        using var f = new InMemoryServiceFixture();
        await f.Seed();
        await Should.ThrowAsync<NachosValidationException>(() =>
            f.Service.CreateMessagesAsync("W", "S", [new("x", peerId!)], "key"));
        await f.AssertNoMessagesOrPeers("key");
    }

    [Theory]
    [InlineData("future.field")]
    [InlineData("x[y]")]
    [InlineData("123")]
    public async Task DeserializationErrors_RetainArrayIndexesAndLiteralPropertyNames(string field)
    {
        using var f = new InMemoryServiceFixture();
        await f.Seed();
        var nested = new string('[', 70) + "1" + new string(']', 70);
        var body = InMemoryServiceFixture.Json(
            "{\"messages\":[{\"content\":\"x\",\"peer_id\":\"P\",\"" + field + "\":" + nested + "}]}", 100);
        var error = await Should.ThrowAsync<RequestValidationException>(() =>
            f.Service.CreateMessagesResponseAsync("W", "S", body, "key", default));
        error.Errors.ShouldHaveSingleItem().Loc.ShouldBe(new object[] { "body", "messages", 0, field });
        error.Errors[0].Type.ShouldBe("value_error");
        await f.AssertNoMessagesOrPeers("key");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstructionsAndContent_AreCountedOnlyOnceDuringFreshAdmission(bool typed)
    {
        var counter = new RecordingTokenCounter();
        using var f = new InMemoryServiceFixture(counter);
        await f.Seed();
        if (typed)
        {
            await f.Service.CreateMessagesAsync("W", "S",
                [new("hello world", "P", Configuration: new(new(CustomInstructions: "hint")))], "key");
        }
        else
        {
            await f.Service.CreateMessagesResponseAsync("W", "S", InMemoryServiceFixture.Json("""
                {"messages":[{"content":"hello world","peer_id":"P","configuration":{"reasoning":{"custom_instructions":"hint"}}}]}
                """), "key", default);
        }
        counter.Inputs.ShouldBe(["hint", "hello world"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("key")]
    public async Task ValidNullableFieldsAndNaiveDates_KeepExistingDeserializerBehavior(string? key)
    {
        using var f = new InMemoryServiceFixture();
        await f.Seed();
        var body = InMemoryServiceFixture.Json("""
            {"messages":[{"content":"","peer_id":"P","metadata":null,
              "configuration":{"reasoning":{"enabled":null,"custom_instructions":null}},
              "created_at":"2024-01-01T00:00:00"}]}
            """);
        await f.Service.CreateMessagesResponseAsync("W", "S", body, key, default);
        var actual = (await f.Service.ListMessagesAsync("W", "S", null, new())).Items.ShouldHaveSingleItem();
        actual.CreatedAt.ShouldBe(new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)));
    }
}
