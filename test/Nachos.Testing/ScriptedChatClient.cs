using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Nachos.Testing;

/// <summary>
/// A chat client that replays queued responses and records every request, so tests can assert prompt shape without a network.
/// An exhausted script throws, so an unexpected extra call fails the test instead of returning an empty answer.
/// </summary>
public sealed class ScriptedChatClient : IChatClient
{
    private readonly Queue<Func<ChatResponse>> _script = new();
    private readonly List<ChatRequest> _requests = [];
    private readonly object _gate = new();

    public IReadOnlyList<ChatRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToList();
            }
        }
    }

    public ScriptedChatClient EnqueueText(string text, UsageDetails? usage = null) =>
        Enqueue(() => new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { Usage = usage });

    /// <summary>Queues a structured-output answer; <c>GetResponseAsync&lt;T&gt;</c> deserializes it with the same options it uses by default.</summary>
    public ScriptedChatClient EnqueueJson<T>(T payload, UsageDetails? usage = null) =>
        EnqueueText(JsonSerializer.Serialize(payload, AIJsonUtilities.DefaultOptions), usage);

    public ScriptedChatClient EnqueueFailure(Exception exception) =>
        Enqueue(() => throw exception);

    /// <summary>Queues a failure built when the call is made, for failures that depend on what happens during the call.</summary>
    public ScriptedChatClient EnqueueFailure(Func<Exception> createException) =>
        Enqueue(() => throw createException());

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Func<ChatResponse> next;
        lock (_gate)
        {
            _requests.Add(new ChatRequest(messages.ToList(), options));
            if (!_script.TryDequeue(out next!))
            {
                throw new InvalidOperationException(
                    $"ScriptedChatClient has no queued response for request {_requests.Count}.");
            }
        }

        return Task.FromResult(next());
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

    private ScriptedChatClient Enqueue(Func<ChatResponse> response)
    {
        lock (_gate)
        {
            _script.Enqueue(response);
        }

        return this;
    }
}

public sealed record ChatRequest(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options);
