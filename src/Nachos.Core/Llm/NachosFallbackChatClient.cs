using System.ClientModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Nachos.Core.Llm;

/// <summary>
/// Sends each request to the primary client and, on a retryable failure (408, 429, 5xx, timeouts, connection errors),
/// to each fallback in order. Streaming falls back only if the failure happens before the first update.
/// </summary>
public sealed partial class NachosFallbackChatClient : DelegatingChatClient
{
    private readonly IReadOnlyList<IChatClient> _fallbacks;
    private readonly IReadOnlyList<string> _profileNames;
    private readonly ILogger _logger;

    /// <param name="profileNames">Profile names of the primary followed by each fallback, used to report which one failed.</param>
    public NachosFallbackChatClient(
        IChatClient primary, IReadOnlyList<IChatClient> fallbacks, IReadOnlyList<string> profileNames, ILogger logger)
        : base(primary)
    {
        ArgumentNullException.ThrowIfNull(fallbacks);
        ArgumentNullException.ThrowIfNull(profileNames);
        ArgumentNullException.ThrowIfNull(logger);
        if (profileNames.Count != fallbacks.Count + 1)
        {
            throw new ArgumentException("One profile name is required for the primary and each fallback.", nameof(profileNames));
        }

        _fallbacks = fallbacks;
        _profileNames = profileNames;
        _logger = logger;
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
                ReportFallback(attempt, ex);
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
                ReportFallback(attempt, ex);
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

    private void ReportFallback(int failedAttempt, Exception exception)
    {
        var from = _profileNames[failedAttempt];
        var to = _profileNames[failedAttempt + 1];
        var status = FindStatus(exception);
        LogFallback(_logger, exception, from, exception.GetType().Name, status, to);
        Activity.Current?.AddEvent(new ActivityEvent("nachos.llm.fallback", tags: new ActivityTagsCollection
        {
            ["nachos.llm.from_profile"] = from,
            ["nachos.llm.to_profile"] = to,
            ["exception.type"] = exception.GetType().FullName,
            ["http.response.status_code"] = status,
        }));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chat profile {FromProfile} failed with {ExceptionType} (status {Status}); falling back to {ToProfile}.")]
    private static partial void LogFallback(ILogger logger, Exception exception, string fromProfile, string exceptionType, int? status, string toProfile);

    private static int? FindStatus(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case ClientResultException { Status: > 0 } clientResult:
                    return clientResult.Status;
                case HttpRequestException { StatusCode: { } status }:
                    return (int)status;
            }
        }

        return null;
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
