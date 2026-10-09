using Azure.Core;

namespace Nachos.Client;

/// <summary>Connection settings for <see cref="NachosHttpClient"/>.</summary>
/// <remarks>
/// <para>
/// A class rather than a record on purpose: a record's generated <c>ToString</c> would print <see cref="ApiKey"/>.
/// <c>ToString</c> is <see cref="object.ToString"/> (the type name only).
/// </para>
/// <para>
/// The rules on each property are checked by the <see cref="NachosHttpClient"/> constructor
/// (<see cref="ArgumentException"/>) and, under <c>AddNachosClient</c>, at host start and whenever the options are
/// first read (<c>OptionsValidationException</c>). No failure message ever contains a configured value.
/// </para>
/// </remarks>
public sealed class NachosClientOptions
{
    /// <summary>
    /// Absolute <c>http</c> or <c>https</c> root of the Nachos server; <c>v3/...</c> routes are resolved beneath it.
    /// </summary>
    public required Uri BaseAddress { get; set; }

    /// <summary>
    /// A NachosKey token, sent as <c>Authorization: Bearer &lt;key&gt;</c>: non-empty printable ASCII without
    /// whitespace. Null sends no credentials. Ignored when <see cref="Credential"/> is set.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// An Entra credential, preferred over <see cref="ApiKey"/> when set. Every call asks it for a token for
    /// <see cref="Scopes"/> (<see cref="TokenCredential.GetTokenAsync"/> with the call's cancellation token; the
    /// credential does its own caching) and sends <c>Authorization: Bearer &lt;token&gt;</c>; retries of one call reuse
    /// that token. An exception from the credential propagates unchanged: it is not retried, wrapped or mapped. A token
    /// that is empty or not printable ASCII without whitespace is rejected with <see cref="InvalidOperationException"/>
    /// before anything is sent.
    /// </summary>
    public TokenCredential? Credential { get; set; }

    /// <summary>
    /// Entra scopes requested for <see cref="Credential"/>, for example <c>api://nachos/.default</c>. Required when
    /// <see cref="Credential"/> is set: at least one, none blank. Never null. The client copies them at construction.
    /// </summary>
    public string[] Scopes { get; set; } = [];

    /// <summary>
    /// Bound on each attempt of a call (send plus buffering the response body) under <c>AddNachosClient</c>, handed to
    /// <see cref="RetryHandler"/>. Default <see cref="RetryHandler.DefaultAttemptTimeout"/> (30 s);
    /// <see cref="Timeout.InfiniteTimeSpan"/> disables it. Otherwise positive and at most <see cref="int.MaxValue"/>
    /// milliseconds. The overall bound stays <see cref="HttpClient.Timeout"/> (100 s by default), which covers every
    /// attempt and backoff of a call. A <see cref="NachosHttpClient"/> constructed by hand does not use it: pass the
    /// timeout to the <see cref="RetryHandler"/> you build.
    /// </summary>
    public TimeSpan AttemptTimeout { get; set; } = RetryHandler.DefaultAttemptTimeout;

    /// <summary>The rules these options break, as messages that never contain a configured value; empty when valid.</summary>
    internal List<string> Validate()
    {
        var failures = new List<string>();
        if (BaseAddress is not { IsAbsoluteUri: true })
        {
            failures.Add("BaseAddress must be an absolute URI.");
        }
        else if (BaseAddress.Scheme is not ("http" or "https"))
        {
            failures.Add("BaseAddress must use the http or https scheme.");
        }

        if (ApiKey is { } key && !IsBearerValue(key))
        {
            failures.Add("ApiKey must be non-empty printable ASCII without whitespace.");
        }

        if (Scopes is null)
        {
            failures.Add("Scopes must not be null; leave it empty when there is no Credential.");
        }
        else if (Credential is not null && (Scopes.Length == 0 || Scopes.Any(string.IsNullOrWhiteSpace)))
        {
            failures.Add("Scopes must hold at least one non-blank scope when Credential is set.");
        }

        if (!RetryHandler.IsValidAttemptTimeout(AttemptTimeout))
        {
            failures.Add("AttemptTimeout must be positive and at most int.MaxValue milliseconds, or Timeout.InfiniteTimeSpan.");
        }

        return failures;
    }

    /// <summary>True for a value that can follow <c>Bearer </c> in a header: non-empty printable ASCII, no whitespace.</summary>
    internal static bool IsBearerValue(string value) =>
        value.Length > 0 && !value.Any(c => c is < '!' or > '~');
}
