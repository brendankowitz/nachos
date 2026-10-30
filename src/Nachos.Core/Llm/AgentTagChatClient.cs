using System.Diagnostics;
using Microsoft.Extensions.AI;

namespace Nachos.Core.Llm;

/// <summary>Copies <see cref="AgentCallTags"/> onto the current span; it sits inside the OpenTelemetry client so the usage span carries them.</summary>
internal sealed class AgentTagChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Tag(options);
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Tag(options);
        return base.GetStreamingResponseAsync(messages, options, cancellationToken);
    }

    private static void Tag(ChatOptions? options)
    {
        var activity = Activity.Current;
        if (activity is null || options?.AdditionalProperties is not { } properties)
        {
            return;
        }

        foreach (var tag in new[] { AgentCallTags.AgentTag, AgentCallTags.WorkspaceTag })
        {
            if (properties.TryGetValue(tag, out var value))
            {
                activity.SetTag(tag, value);
            }
        }
    }
}
