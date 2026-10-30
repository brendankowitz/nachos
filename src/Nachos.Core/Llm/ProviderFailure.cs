using System.ClientModel;
using System.Net;

namespace Nachos.Core.Llm;

/// <summary>Classifies exceptions from a model provider (chat or embeddings), looking through inner exceptions.</summary>
internal static class ProviderFailure
{
    /// <summary>
    /// True for a failure that retrying later can fix: HTTP 408, 429 or 5xx, a connection error (an
    /// <see cref="HttpRequestException"/> without a status), a <see cref="TimeoutException"/>, or a
    /// <see cref="TaskCanceledException"/> that <paramref name="callerToken"/> did not cause (an HTTP timeout).
    /// </summary>
    public static bool IsTransient(Exception exception, CancellationToken callerToken)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (callerToken.IsCancellationRequested)
        {
            return false;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case ClientResultException { Status: > 0 } clientResult:
                    return IsTransientStatus(clientResult.Status);
                case HttpRequestException { StatusCode: { } status }:
                    return IsTransientStatus((int)status);
                case HttpRequestException:
                case TimeoutException:
                case TaskCanceledException:
                    return true;
            }
        }

        return false;
    }

    /// <summary>The HTTP status of the first exception in the chain that carries one, or null.</summary>
    public static int? StatusOf(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
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

    /// <summary>
    /// True for HTTP 401 or 403, or an <c>Azure.Identity.AuthenticationFailedException</c> (a token could not be acquired),
    /// anywhere in the chain. Usually a misconfiguration, so not <see cref="IsTransient"/>; but a new role assignment
    /// also fails this way for minutes until it propagates.
    /// </summary>
    public static bool IsAuthorizationFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (StatusOf(exception) is (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden)
        {
            return true;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            // Matched by name, so Core takes no Azure.Identity dependency; base types cover its subclasses.
            for (var type = current.GetType(); type is not null; type = type.BaseType)
            {
                if (type.FullName == "Azure.Identity.AuthenticationFailedException")
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsTransientStatus(int status) =>
        status is (int)HttpStatusCode.RequestTimeout or (int)HttpStatusCode.TooManyRequests or >= 500;
}
