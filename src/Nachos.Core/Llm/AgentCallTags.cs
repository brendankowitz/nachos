using Microsoft.Extensions.AI;

namespace Nachos.Core.Llm;

/// <summary>Carries the agent and workspace of an LLM call so telemetry spans can be attributed.</summary>
public static class AgentCallTags
{
    public const string AgentTag = "nachos.agent";
    public const string WorkspaceTag = "nachos.workspace";

    public static ChatOptions CreateOptions(string agent, string workspaceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(agent);
        ArgumentException.ThrowIfNullOrEmpty(workspaceId);
        return new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [AgentTag] = agent,
                [WorkspaceTag] = workspaceId,
            },
        };
    }
}
