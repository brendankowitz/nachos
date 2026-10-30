using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Shouldly;

namespace Nachos.Api.Tests;

/// <summary>
/// The in-memory provider runs the worker loops inside the API process, with the offline fake AI, so a posted message
/// becomes conclusions with no other host. Fake AI makes the Deriver eager, so one short message is due at once.
/// </summary>
public sealed class InMemoryWorkerTests : ApiTest
{
    protected override HttpClient CreateClient() => Factory.WithWebHostBuilder(builder =>
        builder.UseSetting("Worker:IdleDelay", "00:00:00.050").UseSetting("Worker:MaxIdleDelay", "00:00:00.100")).CreateClient();

    [Fact]
    public async Task InMemory_PostedMessage_BecomesConclusion_WithFakeAI()
    {
        await SeedSession();
        await Post(M, """{"messages":[{"content":"alice likes green tea","peer_id":"alice"}]}""", 201);

        var conclusion = await WaitForConclusionAsync("alice likes green tea");

        conclusion.GetProperty("observer_id").GetString().ShouldBe("alice");
        conclusion.GetProperty("observed_id").GetString().ShouldBe("alice");
        conclusion.GetProperty("level").GetString().ShouldBe("explicit");

        // The derived conclusion is embedded by the fake generator, so a semantic query finds it.
        var found = await Post(W + "/conclusions/query", """{"query":"alice likes green tea"}""");
        found.GetArrayLength().ShouldBeGreaterThan(0);
    }

    private async Task<JsonElement> WaitForConclusionAsync(string content)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var listed = await Post(W + "/conclusions/list");
            foreach (var conclusion in listed.GetProperty("items").EnumerateArray())
            {
                if (conclusion.GetProperty("content").GetString() == content)
                {
                    return conclusion;
                }
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The in-process worker produced conclusion within 30 seconds. Queue: " + (await Send(HttpMethod.Get, W + "/queue/status")));
            }

            await Task.Delay(100);
        }
    }
}
