using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Nachos.Core.Llm;
using Nachos.Testing;

namespace Nachos.Core.Tests.Llm;

public sealed class TelemetryTests
{
    [Fact]
    public async Task TokenUsageCarriesAgentAndWorkspaceTags()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == NachosChatClients.TelemetrySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var provider = new ScriptedChatClient().EnqueueText("ok", new UsageDetails { InputTokenCount = 12, OutputTokenCount = 3 });
        var options = new LlmOptions { Profiles = { [LlmOptions.DefaultProfileName] = new ModelProfile() } };
        using var clients = new NachosChatClients(options, _ => provider, NullLogger.Instance);

        await clients.ForAgent("deriver").GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            AgentCallTags.CreateOptions("deriver", "ws-1"),
            CancellationToken.None);

        var span = spans.ShouldHaveSingleItem();
        span.GetTagItem("gen_ai.usage.input_tokens").ShouldBe(12);
        span.GetTagItem("gen_ai.usage.output_tokens").ShouldBe(3);
        span.GetTagItem(AgentCallTags.AgentTag).ShouldBe("deriver");
        span.GetTagItem(AgentCallTags.WorkspaceTag).ShouldBe("ws-1");
    }
}
