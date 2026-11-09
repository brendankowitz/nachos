using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Nachos.Core.Deriver;
using Nachos.Core.Summarizer;

namespace Nachos.Core.Llm;

/// <summary>
/// The chat client of fake-AI mode (<c>UseFakeAI</c>). It answers a <see cref="DeriverService"/> request with one fact per
/// <c>target</c> message of its transcript, the message's content verbatim, sourced from that message, and a
/// <see cref="SummarizerService"/> request with the first characters of the newest messages (<see cref="ShortSummaryLength"/>
/// or <see cref="LongSummaryLength"/>). Not a memory-quality mode; it only lets the pipelines run without a model.
/// </summary>
internal sealed class FakeChatClient : IChatClient
{
    internal const int ShortSummaryLength = 200;
    internal const int LongSummaryLength = 600;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();
        var agent = options?.AdditionalProperties?.TryGetValue(AgentCallTags.AgentTag, out var tagged) == true ? tagged as string : null;
        if (agent is not (DeriverService.AgentName or SummarizerService.AgentName))
        {
            throw new NotSupportedException(
                $"UseFakeAI only answers the '{DeriverService.AgentName}' and '{SummarizerService.AgentName}' agents; configure a real chat client for other agents.");
        }

        var request = JsonNode.Parse(messages.Last(message => message.Role == ChatRole.User).Text)!;
        var answer = agent == DeriverService.AgentName ? Derive(request) : Summarize(request);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));
    }

    private static string Derive(JsonNode request)
    {
        var facts = request["messages"]!.AsArray()
            .Select(node => node!.AsObject())
            .Where(entry => entry["role"]?.GetValue<string>() == "target")
            .Select(entry => new ExtractedFact(entry["content"]!.GetValue<string>(), [entry["id"]!.GetValue<string>()]))
            .ToList();
        return JsonSerializer.Serialize(new ExtractionResult(facts), AIJsonUtilities.DefaultOptions);
    }

    private static string Summarize(JsonNode request)
    {
        var length = request["summary_kind"]!.GetValue<string>() == "long" ? LongSummaryLength : ShortSummaryLength;
        var summary = new StringBuilder();
        foreach (var message in request["messages"]!.AsArray().Reverse())
        {
            summary.Append(summary.Length == 0 ? "" : " ").Append(message!["content"]!.GetValue<string>());
            if (summary.Length >= length)
            {
                break;
            }
        }

        return summary.Length <= length ? summary.ToString() : summary.ToString(0, length);
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
