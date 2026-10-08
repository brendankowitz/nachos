using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;
using Nachos.Core.Configuration;
using Nachos.Core.Idempotency;
using Nachos.Core.Keys;
using Nachos.Core.Tokens;
using Nachos.Core.Validation;
using NSubstitute;
using Shouldly;

namespace Nachos.Core.Tests;

public sealed class MessageServiceTests
{
    private const string Body = """{"messages":[{"content":"hello world","peer_id":"P","metadata":null,"configuration":null,"created_at":null}]}""";
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public async Task TypedAndEnvelopeCreation_ShareHashTokensAndCapturedResponse()
    {
        var f = new Fixture();
        var typed = await f.Service.CreateMessagesAsync("W", "S", [new("hello world", "P")], "key");
        var replay = await f.Service.CreateMessagesResponseAsync("W", "S", Json(Body), "key", default);

        f.Commits.ShouldBe(1);
        f.Inputs.ShouldHaveSingleItem().TokenCount.ShouldBe(2);
        typed.ShouldHaveSingleItem().Id.ShouldBe("Public-1");
        typed[0].PeerId.ShouldBe("P");
        typed[0].SessionId.ShouldBe("S");
        typed[0].WorkspaceId.ShouldBe("W");
        typed[0].TokenCount.ShouldBe(2);
        replay.Status.ShouldBe(201);
        replay.Body.ShouldBe(f.Saved!.ResponseBody);
        f.Write!.Ttl.ShouldBe(TimeSpan.FromHours(24));
        f.Write.RequestHash.ShouldBe(RequestHasher.Hash("POST", "/v3/workspaces/{w}/sessions/{s}/messages/",
            new Dictionary<string, string> { ["w"] = "W", ["s"] = "S" }, CanonicalJson.Serialize(Json(Body))));
    }

    [Fact]
    public async Task FirstResponseAndReplay_UseTransactionCaptureNotMutatedRows()
    {
        var f = new Fixture { MutateAfterCapture = true };
        var first = await f.Service.CreateMessagesResponseAsync("W", "S", Json(Body), "key", default);
        var replay = await f.Service.CreateMessagesResponseAsync("W", "S", Json(Body), "key", default);

        first.ShouldBe(replay);
        first.Body.ShouldNotContain("mutated");
        first.Body.ShouldBe(f.Saved!.ResponseBody);
        f.Rows[0].Metadata["changed"]!.GetValue<string>().ShouldBe("mutated");
        f.Commits.ShouldBe(1);
        await f.Store.Messages.DidNotReceiveWithAnyArgs().GetAsync(default!, default!, default!, default);
    }

    [Theory]
    [InlineData("""{"messages":[{"content":"hello world","peer_id":"P"}]}""")]
    [InlineData("""{"messages":[{"content":"hello world","peer_id":"P","metadata":null,"configuration":null,"created_at":null}],"unknown":1}""")]
    [InlineData("""{"messages":[{"content":"hello world","peer_id":"P","metadata":null,"configuration":{},"created_at":null}]}""")]
    [InlineData("""{"messages":[{"content":"hello world","peer_id":"P","metadata":{},"configuration":null,"created_at":null}]}""")]
    [InlineData("""{"messages":[{"content":"hello world","peer_id":"P","metadata":null,"configuration":null,"created_at":null,"unknown":true}]}""")]
    public async Task UnknownOmittedNullAndConfigurationDifferences_AreDifferentRequests(string different)
    {
        var f = new Fixture();
        await f.Service.CreateMessagesResponseAsync("W", "S", Json(Body), "key", default);

        await Should.ThrowAsync<IdempotencyKeyReusedException>(() =>
            f.Service.CreateMessagesResponseAsync("W", "S", Json(different), "key", default));

        f.Commits.ShouldBe(1);
    }

    [Fact]
    public async Task PropertyOrder_DoesNotChangeRequestIdentity()
    {
        var f = new Fixture();
        var first = await f.Service.CreateMessagesResponseAsync("W", "S", Json(Body), "key", default);
        var second = await f.Service.CreateMessagesResponseAsync("W", "S",
            Json("""{"messages":[{"created_at":null,"configuration":null,"metadata":null,"peer_id":"P","content":"hello world"}]}"""), "key", default);
        second.ShouldBe(first);
        f.Commits.ShouldBe(1);
    }

    [Fact]
    public async Task SessionValuesRemainCaseSensitiveAndParticipateInHash()
    {
        var f = new Fixture();
        await f.Service.CreateMessagesResponseAsync("W", "S", Json(Body), "key", default);

        await Should.ThrowAsync<IdempotencyKeyReusedException>(() =>
            f.Service.CreateMessagesResponseAsync("W", "s", Json(Body), "key", default));
        f.Commits.ShouldBe(1);
    }

    [Fact]
    public async Task Replay_ReturnsStoredStatusAndBodyVerbatim()
    {
        var f = new Fixture();
        await f.Service.CreateMessagesResponseAsync("W", "S", Json(Body), "key", default);
        f.Saved = f.Saved! with { ResponseStatus = 202, ResponseBody = "[  {\"old\": true} ]" };

        var response = await f.Service.CreateMessagesResponseAsync("W", "S", Json(Body), "key", default);

        response.ShouldBe(new CapturedResponse(202, "[  {\"old\": true} ]"));
        f.Commits.ShouldBe(1);
    }

    [Fact]
    public async Task Replay_DoesNotRecomputeTokensForAnAlreadyCapturedBatch()
    {
        var counter = Substitute.For<ITokenCounter>();
        counter.Count("hello world").Returns(2);
        var f = new Fixture(counter);
        var first = await f.Service.CreateMessagesResponseAsync("W", "S", Json(Body), "key", default);
        counter.Count("hello world").Returns(_ => throw new InvalidOperationException("tokenizer unavailable"));

        var replay = await f.Service.CreateMessagesResponseAsync("W", "S", Json(Body), "key", default);

        replay.ShouldBe(first);
        f.Commits.ShouldBe(1);
    }

    [Fact]
    public async Task ExpiredKey_IsFreshAtTheExactTtl()
    {
        var f = new Fixture();
        await f.Service.CreateMessagesResponseAsync("W", "S", Json(Body), "key", default);
        f.Clock.Advance(TimeSpan.FromHours(24));

        await f.Service.CreateMessagesAsync("W", "S", [new("different", "P")], "key");

        f.Commits.ShouldBe(2);
        f.Saved!.ExpiresAt.ShouldBe(f.Clock.GetUtcNow().AddHours(24));
    }

    [Fact]
    public async Task DuplicateWithDisappearedWinner_RetriesFreshAtomicAttempt()
    {
        var f = new Fixture { DuplicateOnce = true };

        var response = await f.Service.CreateMessagesResponseAsync("W", "S", Json(Body), "key", default);

        response.Status.ShouldBe(201);
        f.Commits.ShouldBe(1);
        f.Attempts.ShouldBe(2);
    }

    [Fact]
    public async Task DuplicateLoop_ObservesCancellationAndDoesNotReportAConflict()
    {
        var f = new Fixture();
        using var cts = new CancellationTokenSource();
        f.Store.Messages.AppendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<NewMessage>>(),
            Arg.Any<IdempotencyWrite?>(), cts.Token).Returns(_ =>
        {
            cts.Cancel();
            return Task.FromException<IReadOnlyList<MessageRecord>>(new IdempotencyDuplicateException("key"));
        });

        await Should.ThrowAsync<OperationCanceledException>(() =>
            f.Service.CreateMessagesResponseAsync("W", "S", Json(Body), "key", cts.Token));
    }

    [Fact]
    public async Task ConcurrentSameKey_EightContendersHaveOneCommit_RepeatedTwentyTimes()
    {
        for (var round = 0; round < 20; round++)
        {
            var f = new Fixture();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reads = 0;
            f.Store.Idempotency.TryGetAsync("W", "key", default).Returns(async _ =>
            {
                var snapshot = f.Saved;
                var count = Interlocked.Increment(ref reads);
                if (count <= 8)
                {
                    if (count == 8)
                    {
                        ready.SetResult();
                    }
                    await ready.Task;
                }
                return snapshot;
            });

            var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                f.Service.CreateMessagesResponseAsync("W", "S", Json(Body), "key", default)))
                .WaitAsync(TimeSpan.FromSeconds(10));

            f.Commits.ShouldBe(1);
            f.Attempts.ShouldBe(8);
            responses.Distinct().ShouldHaveSingleItem();
            responses[0].Body.ShouldBe(f.Saved!.ResponseBody);
            responses[0].Status.ShouldBe(201);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("non-ascii-\u00e9")]
    public async Task InvalidKeys_AreRejectedWithoutEvenReadingAStore(string key)
    {
        var f = new Fixture();
        f.Store.ClearReceivedCalls();
        f.Store.Sessions.ClearReceivedCalls();
        f.Store.Idempotency.ClearReceivedCalls();

        await Should.ThrowAsync<NachosValidationException>(() => f.Service.CreateMessagesResponseAsync("W", "S", Json(Body), key, default));
        f.Attempts.ShouldBe(0);
        f.Store.Sessions.ReceivedCalls().ShouldBeEmpty();
        f.Store.Idempotency.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task KeyLengthBoundary_Is255AsciiCharacters()
    {
        var f = new Fixture();
        await Should.ThrowAsync<NachosValidationException>(() =>
            f.Service.CreateMessagesResponseAsync("W", "S", Json(Body), new string('k', 256), default));
        f.Attempts.ShouldBe(0);
        (await f.Service.CreateMessagesResponseAsync("W", "S", Json(Body), new string('k', 255), default)).Status.ShouldBe(201);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("""{"messages":null}""")]
    [InlineData("""{"messages":[]}""")]
    [InlineData("""{"messages":[null]}""")]
    [InlineData("""{"messages":[{"peer_id":"P"}]}""")]
    [InlineData("""{"messages":[{"content":12,"peer_id":"P"}]}""")]
    public async Task InvalidEnvelopeShape_HasNoAppend(string body)
    {
        var f = new Fixture();
        await Should.ThrowAsync<RequestValidationException>(() =>
            f.Service.CreateMessagesResponseAsync("W", "S", Json(body), null, default));
        f.Attempts.ShouldBe(0);
    }

    [Fact]
    public async Task BatchAndContentAndInstructionBudgets_AreCheckedBeforeAppend()
    {
        var f = new Fixture();
        await Should.ThrowAsync<RequestValidationException>(() =>
            f.Service.CreateMessagesAsync("W", "S", Enumerable.Repeat(new MessageCreate("ok", "P"), 101).ToArray()));
        await Should.ThrowAsync<RequestValidationException>(() =>
            f.Service.CreateMessagesAsync("W", "S", [new(new string('a', 25001), "P")]));
        await Should.ThrowAsync<NachosValidationException>(() =>
            f.Service.CreateMessagesAsync("W", "S", [new("ok", "bad space")]));
        await Should.ThrowAsync<NachosValidationException>(() =>
            f.Service.CreateMessagesAsync("W", "S", [new("ok", "P", Configuration: new(new(CustomInstructions: string.Concat(Enumerable.Repeat("hello ", 2100)))))]));
        f.Attempts.ShouldBe(0);
    }

    [Fact]
    public async Task TypedProjection_RejectsOpaqueMetadataWithoutRunningGetters()
    {
        var f = new Fixture();
        var opaque = new Opaque();
        await Should.ThrowAsync<NachosValidationException>(() => f.Service.CreateMessagesAsync("W", "S",
            [new("ok", "P", new() { ["opaque"] = JsonValue.Create(opaque) })]));
        opaque.Calls.ShouldBe(0);
        f.Attempts.ShouldBe(0);
    }

    [Fact]
    public async Task TypedProjection_IgnoresAttachedConvertersAndDetachesMetadata()
    {
        var f = new Fixture();
        var converter = new ScalarConverter();
        var options = new JsonSerializerOptions { Converters = { converter }, TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        var metadata = new JsonObject { ["value"] = JsonValue.Create("original", (JsonTypeInfo<string>)options.GetTypeInfo(typeof(string))) };
        var result = await f.Service.CreateMessagesAsync("W", "S", [new("ok", "P", metadata)], "key");
        metadata["value"] = "changed";

        converter.Calls.ShouldBe(0);
        f.Inputs[0].Metadata!["value"]!.GetValue<string>().ShouldBe("original");
        result[0].Metadata["value"]!.GetValue<string>().ShouldBe("original");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("key")]
    public async Task CreationAndReplay_PreserveNonDefaultCreatedAtAndResponseShape(string? key)
    {
        var f = new Fixture();
        var createdAt = f.Clock.GetUtcNow().AddDays(-3).AddTicks(7);

        var result = await f.Service.CreateMessagesAsync("W", "S", [new("", "P", CreatedAt: createdAt)], key);
        var second = await f.Service.CreateMessagesAsync("W", "S", [new("", "P", CreatedAt: createdAt)], key);

        f.Commits.ShouldBe(key is null ? 2 : 1);
        if (key is null)
        {
            f.Write.ShouldBeNull();
            f.Store.Idempotency.ReceivedCalls().ShouldBeEmpty();
        }
        f.Inputs[0].CreatedAt.ShouldBe(createdAt);
        result[0].CreatedAt.ShouldBe(createdAt);
        second[0].CreatedAt.ShouldBe(createdAt);
        result[0].Content.ShouldBe("");
        result[0].TokenCount.ShouldBe(0);
        JsonSerializer.SerializeToElement(result[0]).EnumerateObject().Select(p => p.Name)
            .ShouldBe(["id", "content", "peer_id", "session_id", "metadata", "created_at", "workspace_id", "token_count"]);
    }

    [Theory]
    [InlineData(64)]
    [InlineData(65)]
    public async Task TypedMetadata_KeepsDefault64LimitDespiteEnvelopeContainers(int depth)
    {
        var f = new Fixture();
        var metadata = new JsonObject { ["value"] = 1 };
        for (var i = 1; i < depth; i++)
        {
            metadata = new JsonObject { ["next"] = metadata };
        }
        if (depth == 64)
        {
            var result = await f.Service.CreateMessagesAsync("W", "S", [new("ok", "P", metadata)], "key");
            JsonNode.DeepEquals(result[0].Metadata, metadata).ShouldBeTrue();
            f.Commits.ShouldBe(1);
        }
        else
        {
            await Should.ThrowAsync<NachosValidationException>(() => f.Service.CreateMessagesAsync("W", "S", [new("ok", "P", metadata)], "key"));
            f.Attempts.ShouldBe(0);
        }
    }

    [Fact]
    public async Task BatchAndUnicodeScalarLimits_AcceptInclusiveBoundaries()
    {
        var f = new Fixture();
        var messages = Enumerable.Repeat(new MessageCreate("", "P"), 100).ToArray();
        messages[0] = new(string.Concat(Enumerable.Repeat("\U0001F600", 25000)), "P");

        var result = await f.Service.CreateMessagesAsync("W", "S", messages);

        result.Count.ShouldBe(100);
        result[0].Content.ShouldBe(messages[0].Content);
        f.Inputs[0].TokenCount.ShouldBeGreaterThan(0);
        f.Commits.ShouldBe(1);
    }

    [Fact]
    public async Task MisbehavingStoreWithoutResponseCallback_IsNotReportedAsSuccessfulCapture()
    {
        var f = new Fixture();
        f.Store.Messages.AppendAsync("W", "S", Arg.Any<IReadOnlyList<NewMessage>>(), Arg.Any<IdempotencyWrite?>(), default)
            .Returns(Task.FromResult<IReadOnlyList<MessageRecord>>([]));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            f.Service.CreateMessagesResponseAsync("W", "S", Json(Body), "key", default));
    }

    [Fact]
    public async Task MissingParentAndIndependentStoreFailure_Propagate()
    {
        var f = new Fixture();
        f.Store.Sessions.GetAsync("W", "missing", default).Returns((SessionRecord?)null);
        await Should.ThrowAsync<NotFoundException>(() => f.Service.CreateMessagesResponseAsync("W", "missing", Json(Body), "key", default));
        f.Attempts.ShouldBe(0);
        var failure = new InvalidOperationException("provider unavailable");
        f.Store.Messages.AppendAsync("W", "S", Arg.Any<IReadOnlyList<NewMessage>>(), Arg.Any<IdempotencyWrite?>(), default)
            .Returns(Task.FromException<IReadOnlyList<MessageRecord>>(failure));
        (await Should.ThrowAsync<InvalidOperationException>(() => f.Service.CreateMessagesResponseAsync("W", "S", Json(Body), null, default)))
            .ShouldBeSameAs(failure);
    }

    private sealed class Fixture
    {
        private readonly object _gate = new();
        public IMemoryStore Store { get; } = Substitute.For<IMemoryStore>();
        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UnixEpoch);
        public NachosService Service { get; }
        public IdempotencyRecord? Saved { get; set; }
        public IdempotencyWrite? Write { get; private set; }
        public IReadOnlyList<NewMessage> Inputs { get; private set; } = [];
        public MessageRecord[] Rows { get; private set; } = [];
        public int Commits { get; private set; }
        public int Attempts { get; private set; }
        public bool MutateAfterCapture { get; init; }
        public bool DuplicateOnce { get; init; }

        public Fixture(ITokenCounter? tokenCounter = null)
        {
            var counter = tokenCounter ?? new TiktokenTokenCounter();
            Service = new(Store, counter, new RequestValidator(Options.Create(new NachosOptions()), counter), Substitute.For<IKeyIssuer>());
            Store.Sessions.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(info => new SessionRecord(info.ArgAt<string>(0), info.ArgAt<string>(1), LifecycleState.Active, new(), new(), Clock.GetUtcNow()));
            Store.Idempotency.TryGetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => Saved is { } record && record.ExpiresAt > Clock.GetUtcNow() ? record : null);
            Store.Messages.AppendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<NewMessage>>(),
                Arg.Any<IdempotencyWrite?>(), Arg.Any<CancellationToken>()).Returns(info =>
            {
                lock (_gate)
                {
                    info.Arg<CancellationToken>().ThrowIfCancellationRequested();
                    Attempts++;
                    var write = info.Arg<IdempotencyWrite?>();
                    if (write is not null && ((DuplicateOnce && Attempts == 1) || Saved is { } record && record.ExpiresAt > Clock.GetUtcNow()))
                    {
                        throw new IdempotencyDuplicateException(write.Key);
                    }
                    Write = write;
                    Inputs = info.Arg<IReadOnlyList<NewMessage>>();
                    Rows = Inputs.Select((m, i) => new MessageRecord($"Public-{i + 1}", info.ArgAt<string>(0),
                        info.ArgAt<string>(1), m.PeerName, i + 1, m.Content, m.TokenCount,
                        m.Metadata?.DeepClone().AsObject() ?? new(), m.CreatedAt ?? Clock.GetUtcNow())).ToArray();
                    if (write is not null)
                    {
                        Saved = new(write.Key, write.RequestHash, write.ResponseStatus, write.SerializeResponse(Rows), Clock.GetUtcNow() + write.Ttl);
                    }
                    if (MutateAfterCapture)
                    {
                        Rows[0].Metadata["changed"] = "mutated";
                    }
                    Commits++;
                    return Task.FromResult<IReadOnlyList<MessageRecord>>(Rows);
                }
            });
        }
    }

    private sealed class Opaque
    {
        public int Calls { get; private set; }
        public string Value { get { Calls++; return "opaque"; } }
    }

    private sealed class ScalarConverter : JsonConverter<string>
    {
        public int Calls { get; private set; }
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            Calls++;
            writer.WriteStringValue("converted");
        }
    }
}
