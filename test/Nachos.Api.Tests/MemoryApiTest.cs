using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Nachos.Core.Llm;

namespace Nachos.Api.Tests;

/// <summary>
/// An API host with an offline embedding generator, so semantic reads work. Workspace <c>w</c> with peers alice and
/// bob and session <c>s</c> is created by <see cref="SeedPeers"/>.
/// </summary>
public abstract class MemoryApiTest : ApiTest
{
    protected const string C = W + "/conclusions";
    protected const string Alice = W + "/peers/alice";

    protected override HttpClient CreateClient() => Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new DeterministicEmbeddingGenerator()))).CreateClient();

    protected async Task SeedPeers()
    {
        await SeedSession();
        await Post(W + "/peers", """{"id":"alice"}""");
        await Post(W + "/peers", """{"id":"bob"}""");
    }

    protected Task<System.Text.Json.JsonElement> Conclude(string content, string observer = "alice", string observed = "alice",
        string? session = null) =>
        Post(C, $$"""{"conclusions":[{"content":"{{content}}","observer_id":"{{observer}}","observed_id":"{{observed}}"{{(session is null ? "" : $",\"session_id\":\"{session}\"")}}}]}""", 201);
}
