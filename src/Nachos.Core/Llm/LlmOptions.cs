namespace Nachos.Core.Llm;

/// <summary>Bound from the <c>Llm</c> configuration section by <c>UseChatClient</c>:<c>Llm:Profiles:{name}</c> and <c>Llm:Agents:{agent}</c> (the profile name an agent uses).</summary>
public sealed class LlmOptions
{
    public const string DefaultProfileName = "default";

    public Dictionary<string, ModelProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Agents { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
