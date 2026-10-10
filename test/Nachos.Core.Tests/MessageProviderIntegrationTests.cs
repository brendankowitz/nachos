using Microsoft.Extensions.DependencyInjection;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Core.Idempotency;
using Nachos.Core.Tokens;
using Shouldly;

namespace Nachos.Core.Tests;

public sealed class MessageProviderIntegrationTests
{
    private const string Body = """{"messages":[{"content":"hello world","peer_id":"P"}]}""";

    [Fact]
    public async Task ResponseCaptureReplayAndMembership_UseTheActualProviderAndScopedClient()
    {
        var counter = new RecordingTokenCounter();
        using var f = new InMemoryServiceFixture(counter);
        await f.Seed();
        f.Scope.ServiceProvider.GetRequiredService<INachosClient>().ShouldBeSameAs(f.Service);
        using var other = f.Provider.CreateScope();
        other.ServiceProvider.GetRequiredService<INachosClient>().ShouldNotBeSameAs(f.Service);
        await f.Service.AddSessionPeersAsync("W", "S", new Dictionary<string, SessionPeerConfig> { ["P"] = new(false, true) });
        await f.Service.RemoveSessionPeersAsync("W", "S", ["P"]);
        var createdAt = f.Clock.GetUtcNow().AddDays(-7).AddTicks(13);
        var input = new MessageCreate("hello world", "P", CreatedAt: createdAt);
        var first = (await f.Service.CreateMessagesAsync("W", "S", [input], "key")).ShouldHaveSingleItem();
        first.CreatedAt.ShouldBe(createdAt);
        first.TokenCount.ShouldBe(2);
        var captured = (await f.Store.Idempotency.TryGetAsync("W", "key", default))!;
        captured.ResponseStatus.ShouldBe(201);
        captured.ExpiresAt.ShouldBe(f.Clock.GetUtcNow().AddHours(24));
        captured.ResponseBody.ShouldBe(System.Text.Json.JsonSerializer.Serialize(new[] { first }));
        (await f.Store.Messages.GetAsync("W", "S", first.Id, default))!.CreatedAt.ShouldBe(createdAt);
        (await f.Service.GetSessionPeerConfigAsync("W", "S", "P")).ShouldBe(new(false, true));
        (await f.Service.ListSessionPeersAsync("W", "S", new())).Items.ShouldHaveSingleItem().Id.ShouldBe("P");

        await f.Service.UpdateMessageAsync("W", "S", first.Id, new() { ["later"] = true });
        counter.Failure = new InvalidOperationException("a replay must not recount content");
        var replay = (await f.Service.CreateMessagesAsync("W", "S", [input], "key")).ShouldHaveSingleItem();
        replay.CreatedAt.ShouldBe(createdAt);
        replay.Id.ShouldBe(first.Id);
        replay.Metadata.ShouldBeEmpty();
        (await f.Store.Idempotency.TryGetAsync("W", "key", default))!.ResponseBody.ShouldBe(captured.ResponseBody);
        counter.Inputs.ShouldBe(["hello world"]);
    }

    [Fact]
    public async Task ReuseNamespaceAndExactExpiry_AreEnforcedByTheRealStore()
    {
        using var f = new InMemoryServiceFixture();
        await f.Seed();
        var first = await f.Service.CreateMessagesResponseAsync("W", "S", InMemoryServiceFixture.Json(Body), "key", default);
        await f.Service.GetOrCreateSessionAsync("W", "other");
        await Should.ThrowAsync<IdempotencyKeyReusedException>(() => f.Service.CreateMessagesResponseAsync(
            "W", "other", InMemoryServiceFixture.Json(Body), "key", default));
        await f.Service.GetOrCreateWorkspaceAsync("w");
        await f.Service.GetOrCreateSessionAsync("w", "S");
        (await f.Service.CreateMessagesResponseAsync("w", "S", InMemoryServiceFixture.Json(Body), "key", default)).Status.ShouldBe(201);
        f.Clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromTicks(1));
        (await f.Service.CreateMessagesResponseAsync("W", "S", InMemoryServiceFixture.Json(Body), "key", default)).ShouldBe(first);
        var changed = InMemoryServiceFixture.Json(Body.Replace("hello world", "different", StringComparison.Ordinal));
        await Should.ThrowAsync<IdempotencyKeyReusedException>(() =>
            f.Service.CreateMessagesResponseAsync("W", "S", changed, "key", default));
        f.Clock.Advance(TimeSpan.FromTicks(1));
        (await f.Service.CreateMessagesResponseAsync("W", "S", changed, "key", default)).Status.ShouldBe(201);
        (await f.Service.ListMessagesAsync("W", "S", null, new())).Total.ShouldBe(2);
        (await f.Store.Idempotency.TryGetAsync("W", "key", default))!.ExpiresAt.ShouldBe(f.Clock.GetUtcNow().AddHours(24));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EightConcurrentScopes_HaveOneCommitAcrossTwentyRounds(bool differentBodies)
    {
        for (var round = 0; round < 20; round++)
        {
            using var counter = new GatedCounter();
            using var f = new InMemoryServiceFixture(counter);
            await f.Seed();
            var tasks = Enumerable.Range(0, 8).Select(index => Task.Factory.StartNew(async () =>
            {
                using var scope = f.Provider.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<NachosService>();
                var body = differentBodies ? Body.Replace("hello world", $"message {index}", StringComparison.Ordinal) : Body;
                try
                {
                    return await service.CreateMessagesResponseAsync("W", "S", InMemoryServiceFixture.Json(body), "key", default);
                }
                catch (IdempotencyKeyReusedException) when (differentBodies)
                {
                    return null;
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap()).ToArray();
            var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
            var responses = results.OfType<CapturedResponse>().ToArray();
            responses.Length.ShouldBe(differentBodies ? 1 : 8);
            responses.Distinct().ShouldHaveSingleItem();
            counter.Calls.ShouldBe(8);
            var rows = await f.Store.Messages.ListAsync("W", "S", null, new(), default);
            rows.Items.ShouldHaveSingleItem().Seq.ShouldBe(1);
            (await f.Service.ListSessionPeersAsync("W", "S", new())).Total.ShouldBe(1);
            var saved = (await f.Store.Idempotency.TryGetAsync("W", "key", default))!;
            saved.ResponseStatus.ShouldBe(responses[0].Status);
            saved.ResponseBody.ShouldBe(responses[0].Body);
        }
    }

    private sealed class GatedCounter : ITokenCounter, IDisposable
    {
        private readonly Barrier _barrier = new(8);
        private readonly TiktokenTokenCounter _actual = new();
        private int _calls;
        public int Calls => _calls;
        public int Count(string text)
        {
            Interlocked.Increment(ref _calls);
            if (!_barrier.SignalAndWait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Eight cache misses did not arrive.");
            return _actual.Count(text);
        }
        public void Dispose() => _barrier.Dispose();
    }
}
