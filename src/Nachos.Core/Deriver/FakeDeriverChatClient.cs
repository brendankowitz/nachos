using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Nachos.Core.Llm;

namespace Nachos.Core.Deriver;

/// <summary>
/// The chat client of fake-AI mode (<c>UseFakeAI</c>): answers a <see cref="DeriverService"/> request with one fact per
/// <c>target</c> message of its transcript, the message's content verbatim, sourced from that message. Not a
/// memory-quality mode; it only lets the derivation pipeline run without a model.
/// </summary>
internal sealed class FakeDeriverChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();
        if (options?.AdditionalProperties?.TryGetValue(AgentCallTags.AgentTag, out var agent) != true
            || !Equals(agent, DeriverService.AgentName))
        {
            throw new NotSupportedException(
                $"UseFakeAI only answers the '{DeriverService.AgentName}' agent; configure a real chat client for other agents.");
        }

        var transcript = messages.Last(message => message.Role == ChatRole.User).Text;
        var facts = JsonNode.Parse(transcript)!["messages"]!.AsArray()
            .Select(node => node!.AsObject())
            .Where(entry => entry["role"]?.GetValue<string>() == "target")
            .Select(entry => new ExtractedFact(entry["content"]!.GetValue<string>(), [entry["id"]!.GetValue<string>()]))
            .ToList();
        var answer = JsonSerializer.Serialize(new ExtractionResult(facts), AIJsonUtilities.DefaultOptions);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
