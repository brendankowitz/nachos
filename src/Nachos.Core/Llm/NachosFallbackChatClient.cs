using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using System.ClientModel;

namespace Nachos.Core.Llm;

/// <summary>
/// Sends each request to the primary client and, on a retryable failure (408, 429, 5xx, timeouts, connection errors),
/// to each fallback in order. Streaming falls back only if the failure happens before the first update.
/// </summary>
public sealed class NachosFallbackChatClient : DelegatingChatClient
{
    private readonly IReadOnlyList<IChatClient> _fallbacks;

    public NachosFallbackChatClient(IChatClient primary, IReadOnlyList<IChatClient> fallbacks)
        : base(primary)
    {
        ArgumentNullException.ThrowIfNull(fallbacks);
        _fallbacks = fallbacks;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var chat = messages.ToList();
        var attempt = 0;
        while (true)
        {
            var client = attempt == 0 ? InnerClient : _fallbacks[attempt - 1];
            try
            {
                return await client.GetResponseAsync(chat, options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < _fallbacks.Count && IsRetryable(ex, cancellationToken))
            {
                attempt++;
            }
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var chat = messages.ToList();
        var attempt = 0;
        while (true)
        {
            var client = attempt == 0 ? InnerClient : _fallbacks[attempt - 1];
            await using var updates = client.GetStreamingResponseAsync(chat, options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            bool hasFirst;
            try
            {
                hasFirst = await updates.MoveNextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < _fallbacks.Count && IsRetryable(ex, cancellationToken))
            {
                attempt++;
                continue;
            }

            while (hasFirst)
            {
                yield return updates.Current;
                hasFirst = await updates.MoveNextAsync().ConfigureAwait(false);
            }

            yield break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var fallback in _fallbacks)
            {
                fallback.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private static bool IsRetryable(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case ClientResultException { Status: > 0 } clientResult:
                    return IsRetryableStatus(clientResult.Status);
                case HttpRequestException { StatusCode: { } status }:
                    return IsRetryableStatus((int)status);
                case HttpRequestException:
                case TimeoutException:
                case TaskCanceledException:
                    return true;
            }
        }

        return false;
    }

    private static bool IsRetryableStatus(int status) =>
        status is (int)HttpStatusCode.RequestTimeout or (int)HttpStatusCode.TooManyRequests or >= 500;
}
