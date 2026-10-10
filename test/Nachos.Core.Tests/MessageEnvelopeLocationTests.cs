using System.Text.Json;
using Microsoft.Extensions.Options;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Stores;
using Nachos.Core.Configuration;
using Nachos.Core.Keys;
using Nachos.Core.Validation;
using NSubstitute;
using Shouldly;

namespace Nachos.Core.Tests;

public sealed class MessageEnvelopeLocationTests
{
    private static readonly JsonSerializerOptions OriginalDtoOptions = new() { MaxDepth = 67 };

    public static TheoryData<string, object[], string?> PermissiveLexicalForms()
    {
        (string Json, object[] Location)[] shapes =
        [
            ("""[{"content":"x","peer_id":"P",}]""", ["body", "messages", 0]),
            ("""[{"content":"x","peer_id":"P"},]""", ["body", "messages"]),
            ("""[/*synthetic*/{"content":"x","peer_id":"P"}]""", ["body", "messages", 0]),
            ("""[{"content":/*synthetic*/"x","peer_id":"P"}]""", ["body", "messages", 0, "content"]),
            ("""[{"content":"x","peer_id":"P","extra":[0,]}]""", ["body", "messages", 0, "extra"]),
            ("""[{"content":"x","peer_id":"P","extra":/*synthetic*/0}]""", ["body", "messages", 0, "extra"]),
            ("""[{/*synthetic*/"content":"x","peer_id":"P"}]""", ["body", "messages", 0]),
            ("""[{"content":"x",/*synthetic*/"peer_id":"P"}]""", ["body", "messages", 0]),
            ("""[{"content":"x","peer_id":"P","configuration":/*synthetic*/{"reasoning":{}}}]""",
                ["body", "messages", 0, "configuration"]),
            ("""[{"content":"x","peer_id":"P","configuration":{"reasoning":{/*synthetic*/"enabled":true}}}]""",
                ["body", "messages", 0, "configuration", "reasoning"]),
            ("""[{"content":"x","peer_id":"P","configuration":{"reasoning":{"enabled":/*synthetic*/true}}}]""",
                ["body", "messages", 0, "configuration", "reasoning", "enabled"]),
            ("""[{"content":"x","peer_id":"P","configuration":{"reasoning":{},}}]""",
                ["body", "messages", 0, "configuration"]),
            ("""[{"content":"x","peer_id":"P","configuration":{"reasoning":{"enabled":true,}}}]""",
                ["body", "messages", 0, "configuration", "reasoning"]),
            ("""[{"content":"x","peer_id":"P"}/*synthetic*/]""", ["body", "messages"]),
            ("""[/*synthetic*/]""", ["body", "messages"]),
            ("[\r\n{\"content\":\"é😀\",\"peer_id\":\"first\"},\r\n" +
                "{\"content\":\"x\",\"peer_id\":\"P\",\"a'][2147483648]\":null," +
                "\"a'][2147483648]\"://synthetic\r\n0}]", ["body", "messages", 1, "a'][2147483648]"]),
        ];
        var cases = new TheoryData<string, object[], string?>();
        foreach (var (json, location) in shapes)
        foreach (var key in new string?[] { null, "key" })
            cases.Add(json, location, key);
        return cases;
    }

    [Theory]
    [MemberData(nameof(PermissiveLexicalForms))]
    public async Task PermissivelyParsedSyntax_RemainsRejectedWithOriginalCauseAndLocation(
        string messages, object[] location, string? key)
    {
        using var document = JsonDocument.Parse("{\"messages\":" + messages + "}",
            new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var body = document.RootElement;
        var original = Should.Throw<JsonException>(() =>
            body.GetProperty("messages").Deserialize<MessageCreate[]>(OriginalDtoOptions));
        var store = Substitute.For<IMemoryStore>();
        var counter = new RecordingTokenCounter();
        var service = new NachosService(store, counter,
            new RequestValidator(Options.Create(new NachosOptions()), counter), Substitute.For<IKeyIssuer>());

        var actual = await Record.ExceptionAsync(() =>
            service.CreateMessagesResponseAsync("W", "S", body, key, default));

        store.ReceivedCalls().ShouldBeEmpty();
        counter.Inputs.ShouldBeEmpty();
        var error = actual.ShouldBeOfType<RequestValidationException>();
        error.Errors.ShouldHaveSingleItem().Loc.ShouldBe(location);
        error.Errors[0].Type.ShouldBe("value_error");
        var cause = error.InnerException.ShouldBeOfType<JsonException>();
        cause.Message.ShouldBe(original.Message);
        cause.Path.ShouldBe(original.Path);
        cause.LineNumber.ShouldBe(original.LineNumber);
        cause.BytePositionInLine.ShouldBe(original.BytePositionInLine);
        cause.InnerException.ShouldNotBeNull();
        cause.InnerException.GetType().ShouldBe(original.InnerException!.GetType());
        cause.InnerException.Message.ShouldBe(original.InnerException.Message);
    }

    public static TheoryData<string, string, string?> LiteralNames()
    {
        string[] names =
        [
            "a.b", "x[y]", "", "a'b", "a']b", "a'].b", "a'][123]", "a\\b", "123",
            "a'][2147483648]", "a'][999999999999999999999999999999]",
            "\"", "\0", "\r\n\t", "é😀", "$", "[0]", "a']['b", "reasoning.enabled",
        ];
        var cases = new TheoryData<string, string, string?>();
        foreach (var name in names)
        foreach (var context in new[] { "message", "configuration", "reasoning" })
        foreach (var key in new string?[] { null, "key" })
            cases.Add(name, context, key);
        return cases;
    }

    [Theory]
    [MemberData(nameof(LiteralNames))]
    public async Task ExistingDepthFailure_PreservesStructuredLiteralLocation(
        string name, string context, string? key)
    {
        var deep = new string('[', 70) + "0" + new string(']', 70);
        var field = JsonSerializer.Serialize(name);
        // Decoys include the same literal name before the failing occurrence and apparent path segments.
        var properties = "\"a\":0,\"b']\":0,\"123\":0," + field + ":null," + field + ":" + deep;
        var location = new List<object> { "body", "messages", 1 };
        if (context == "configuration")
        {
            properties = "\"configuration\":{" + properties + "}";
            location.Add("configuration");
        }
        else if (context == "reasoning")
        {
            properties = "\"configuration\":{\"reasoning\":{" + properties + "}}";
            location.AddRange(["configuration", "reasoning"]);
        }
        location.Add(name);
        // Raw UTF-8 and line breaks before the failure guard byte-vs-character and line offsets.
        var body = InMemoryServiceFixture.Json(
            "[\n{\"content\":\"" + new string('é', 512) +
            "😀\",\"peer_id\":\"first\"},\n{\"content\":\"x\",\"peer_id\":\"P\"," +
            properties + "}]", 100);
        var original = Should.Throw<JsonException>(() => body.Deserialize<MessageCreate[]>(OriginalDtoOptions));
        var envelope = InMemoryServiceFixture.Json("{\"messages\":" + body.GetRawText() + "}", 100);
        var store = Substitute.For<IMemoryStore>();
        var counter = new RecordingTokenCounter();
        var service = new NachosService(store, counter,
            new RequestValidator(Options.Create(new NachosOptions()), counter), Substitute.For<IKeyIssuer>());

        var error = await Should.ThrowAsync<RequestValidationException>(() =>
            service.CreateMessagesResponseAsync("W", "S", envelope, key, default));

        error.Errors.ShouldHaveSingleItem().Loc.ShouldBe(location);
        error.Errors[0].Type.ShouldBe("value_error");
        var cause = error.InnerException.ShouldBeOfType<JsonException>();
        cause.Path.ShouldBe(original.Path);
        cause.LineNumber.ShouldBe(original.LineNumber);
        cause.BytePositionInLine.ShouldBe(original.BytePositionInLine);
        store.ReceivedCalls().ShouldBeEmpty();
        counter.Inputs.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("key")]
    public async Task MetadataDepthFailure_StaysAtDeserializerOwnedField(string? key)
    {
        var deep = new string('[', 70) + "0" + new string(']', 70);
        var body = InMemoryServiceFixture.Json(
            "{\"messages\":[{\"content\":\"x\",\"peer_id\":\"P\",\"metadata\":{\"a'][2147483648]\":" +
            deep + "}}]}", 100);
        using var f = new InMemoryServiceFixture();
        await f.Seed();

        var error = await Should.ThrowAsync<RequestValidationException>(() =>
            f.Service.CreateMessagesResponseAsync("W", "S", body, key, default));

        error.Errors.ShouldHaveSingleItem().Loc.ShouldBe(new object[] { "body", "messages", 0, "metadata" });
        await f.AssertNoMessagesOrPeers(key);
    }
}
