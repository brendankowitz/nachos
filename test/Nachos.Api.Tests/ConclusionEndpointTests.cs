using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nachos.Core.Llm;
using Nachos.Core.Worker;
using System.Text.Json;
using Shouldly;

namespace Nachos.Api.Tests;

public sealed class ConclusionEndpointTests : MemoryApiTest
{
    private static string Batch(int count, string content = "a") => "{\"conclusions\":[" + string.Join(',',
        Enumerable.Repeat($$"""{"content":"{{content}}","observer_id":"alice","observed_id":"bob"}""", count)) + "]}";

    [Fact]
    public async Task Create_Is201_WithExplicitConclusionsInOrder()
    {
        await SeedPeers();
        var created = await Post(C, """
            {"conclusions":[
              {"content":"likes tea","observer_id":"alice","observed_id":"bob","session_id":"s"},
              {"content":"is tall","observer_id":"alice","observed_id":"alice"}]}
            """, 201);

        created.GetArrayLength().ShouldBe(2);
        var first = created[0];
        first.GetProperty("content").GetString().ShouldBe("likes tea");
        first.GetProperty("observer_id").GetString().ShouldBe("alice");
        first.GetProperty("observed_id").GetString().ShouldBe("bob");
        first.GetProperty("session_id").GetString().ShouldBe("s");
        first.GetProperty("level").GetString().ShouldBe("explicit");
        first.GetProperty("source_ids").GetArrayLength().ShouldBe(0);
        created[1].GetProperty("session_id").ValueKind.ShouldBe(JsonValueKind.Null);
        Ids(await Post(C + "/list")).ShouldBe([first.GetProperty("id").GetString()!, created[1].GetProperty("id").GetString()!]);
    }

    [Theory]
    [InlineData(0, 422)]
    [InlineData(1, 201)]
    [InlineData(100, 201)]
    [InlineData(101, 422)]
    public async Task BatchCount_IsBounded_WithoutPartialWrites(int count, int status)
    {
        await SeedPeers();
        var body = await Post(C, Batch(count), status);
        if (status == 422)
        {
            Location(body, "body", "conclusions");
        }
        (await Post(C + "/list")).GetProperty("total").GetInt32().ShouldBe(status == 201 ? count : 0);
    }

    [Theory]
    [InlineData(0, 422)]
    [InlineData(65_535, 201)]
    [InlineData(65_536, 422)]
    public async Task ContentLength_IsBounded(int length, int status)
    {
        await SeedPeers();
        var body = await Post(C, Batch(1, new string('x', length)), status);
        if (status == 422)
        {
            Location(body, "body", "conclusions", 0, "content");
        }
    }

    [Fact]
    public async Task MissingBatch_Is422Missing()
    {
        await SeedPeers();
        var body = await Post(C, "{}", 422);
        Location(body, "body", "conclusions");
        body.GetProperty("detail")[0].GetProperty("type").GetString().ShouldBe("missing");
    }

    [Fact]
    public async Task UnknownPeer_Is404_AndStoresNothing()
    {
        await SeedPeers();
        await Post(C, """{"conclusions":[{"content":"a","observer_id":"alice","observed_id":"bob"},{"content":"b","observer_id":"nobody","observed_id":"bob"}]}""", 404);
        (await Post(C + "/list")).GetProperty("total").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task IdempotencyKey_IsNotSupported_SoARepeatStoresASecondBatch()
    {
        await SeedPeers();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, C) { Content = new StringContent(Batch(1), Encoding.UTF8, "application/json") };
            request.Headers.Add("Idempotency-Key", "same-key");
            using var response = await Client.SendAsync(request);
            ((int)response.StatusCode).ShouldBe(201);
        }
        (await Post(C + "/list")).GetProperty("total").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task List_FiltersPagesAndReverses()
    {
        await SeedPeers();
        await Conclude("one", observed: "bob");
        await Conclude("two");
        await Conclude("three", observed: "bob");

        var page = await Post(C + "/list?page=1&size=1&reverse=true", """{"filters":{"observed_id":"bob"}}""");
        page.GetProperty("total").GetInt32().ShouldBe(2);
        page.GetProperty("pages").GetInt32().ShouldBe(2);
        page.GetProperty("items")[0].GetProperty("content").GetString().ShouldBe("three");
        // Level is not a wire filter field; like any unknown top-level key it is ignored.
        (await Post(C + "/list", """{"filters":{"level":"deductive"}}""")).GetProperty("total").GetInt32().ShouldBe(3);
        Problem(await Post(C + "/list", """{"filters":{"observer_id":{"regex":"a"}}}""", 422), 422, JsonValueKind.String);
    }

    [Fact]
    public async Task Query_ReturnsNearestFirst_WithinTopK()
    {
        await SeedPeers();
        await Conclude("enjoys green tea in the morning");
        await Conclude("owns a red bicycle");

        var found = await Post(C + "/query", """{"query":"enjoys green tea in the morning","top_k":1}""");
        found.GetArrayLength().ShouldBe(1);
        found[0].GetProperty("content").GetString().ShouldBe("enjoys green tea in the morning");
        (await Post(C + "/query", """{"query":"bicycle"}""")).GetArrayLength().ShouldBe(2);
    }

    [Theory]
    [InlineData("""{"query":"q","top_k":0}""", "top_k")]
    [InlineData("""{"query":"q","top_k":101}""", "top_k")]
    [InlineData("""{"query":"q","distance":-0.1}""", "distance")]
    [InlineData("""{"query":"q","distance":1.1}""", "distance")]
    [InlineData("""{"query":"q","top_k":"5"}""", "top_k")]
    [InlineData("""{"top_k":5}""", "query")]
    public async Task Query_Bounds_Are422(string body, string field)
    {
        await SeedPeers();
        Location(await Post(C + "/query", body, 422), "body", field);
    }

    [Fact]
    public async Task GetAndDelete_TombstoneHidesTheConclusion()
    {
        await SeedPeers();
        var id = (await Conclude("temporary"))[0].GetProperty("id").GetString()!;

        (await Send(HttpMethod.Get, C + "/" + id)).GetProperty("content").GetString().ShouldBe("temporary");
        await Send(HttpMethod.Delete, C + "/" + id, status: 204);
        Problem(await Send(HttpMethod.Get, C + "/" + id, status: 404), 404, JsonValueKind.String);
        (await Post(C + "/list")).GetProperty("total").GetInt32().ShouldBe(0);
        await Send(HttpMethod.Delete, C + "/" + id, status: 204);
        await Send(HttpMethod.Delete, C + "/nevercreated", status: 204);
    }
}

/// <summary>
/// A host with no embedding generator, which the API only reaches if someone wires <c>AddNachos</c> by hand: semantic reads
/// answer 503, and everything else still works. The generator, its startup probe and the Deriver that needs it are removed.
/// </summary>
public sealed class ConclusionEndpointWithoutEmbeddingsTests : ApiTest
{
    private readonly WarningCapture _warnings = new();

    protected override HttpClient CreateClient() => Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
    {
        services.AddSingleton<ILoggerProvider>(_warnings);
        services.RemoveAll<IEmbeddingGenerator<string, Embedding<float>>>();
        services.RemoveAll<IWorkHandler>();
        foreach (var probe in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(EmbeddingDimensionCheck)).ToList())
        {
            services.Remove(probe);
        }
    })).CreateClient();

    [Fact]
    public async Task SemanticReads_Are503_ButCreateAndListWork()
    {
        await SeedSession();
        await Post(W + "/peers", """{"id":"alice"}""");
        await Post(W + "/conclusions", """{"conclusions":[{"content":"a","observer_id":"alice","observed_id":"alice"}]}""", 201);

        var query = await Post(W + "/conclusions/query", """{"query":"a"}""", 503);
        Problem(query, 503, JsonValueKind.String);
        query.GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain("no embedding generator");
        Problem(await Post(W + "/peers/alice/representation", """{"search_query":"a"}""", 503), 503, JsonValueKind.String);
        (await Post(W + "/conclusions/list")).GetProperty("total").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task A503_SaysWhenToRetry_AndIsLoggedAsAWarning()
    {
        await SeedSession();

        using var response = await Client.PostAsync(
            W + "/conclusions/query", new StringContent("""{"query":"a"}""", Encoding.UTF8, "application/json"));

        ((int)response.StatusCode).ShouldBe(503);
        response.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromSeconds(30));
        _warnings.Messages.ShouldContain(m => m.Contains("service it depends on is unavailable"));
    }

    private sealed class WarningCapture : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Capture(Messages);

        public void Dispose()
        {
        }

        private sealed class Capture(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning)
                {
                    messages.Enqueue(formatter(state, exception));
                }
            }
        }
    }
}
