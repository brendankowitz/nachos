namespace Nachos.Client;

/// <summary>How a route may be replayed automatically after a transient failure (spec §16).</summary>
public enum RetryCategory
{
    /// <summary>A read, or a write whose repeat leaves the same state (PUT, DELETE, get-or-create by id).</summary>
    Retryable,

    /// <summary>Never replayed: chat, <c>GET H/test</c>, and non-idempotent POSTs that accept no Idempotency-Key.</summary>
    Never,

    /// <summary>A non-idempotent mutation, replayed only when the request carries an <c>Idempotency-Key</c>.</summary>
    RequiresIdempotencyKey,
}

/// <summary>
/// Operation-aware retry rules (spec §16), keyed on the HTTP method and the wire-manifest route template
/// (for example <c>/v3/workspaces/{workspace_id}/sessions/{session_id}/messages</c>).
/// </summary>
/// <remarks>
/// This decides whether a request may be replayed at all. Which outcomes are retried for a replayable request
/// (statuses, and the precedence of a received status over a later body-read failure) is
/// <see cref="RetryHandler"/>'s rule: a non-retryable status is final even if its body then fails to read.
/// </remarks>
public static class RetryClassifier
{
    // POST routes are the only ones whose safety the method does not decide. Each pattern names a route by its
    // literal segments, with "{}" matching any "{parameter}" segment. Read-only POSTs ending in a read verb
    // (list/search/query/representation) are recognised by suffix instead.
    private static readonly (string[] Pattern, RetryCategory Category)[] PostRoutes =
    [
        // Non-idempotent mutations that accept an Idempotency-Key (spec §9.1).
        (Split("v3/workspaces/{}/sessions/{}/messages"), RetryCategory.RequiresIdempotencyKey),
        (Split("v3/workspaces/{}/sessions/{}/messages/upload"), RetryCategory.RequiresIdempotencyKey),
        (Split("v3/workspaces/{}/conclusions"), RetryCategory.RequiresIdempotencyKey),
        (Split("v3/workspaces/{}/sessions/{}/clone"), RetryCategory.RequiresIdempotencyKey),

        // Get-or-create keyed by the body id.
        (Split("v3/workspaces"), RetryCategory.Retryable),
        (Split("v3/workspaces/{}/peers"), RetryCategory.Retryable),
        (Split("v3/workspaces/{}/sessions"), RetryCategory.Retryable),
        (Split("v3/workspaces/{}/scopes"), RetryCategory.Retryable),

        // Read-only listing of a peer's sessions.
        (Split("v3/workspaces/{}/peers/{}/sessions"), RetryCategory.Retryable),

        // Membership add: repeating one leaves the same members.
        (Split("v3/workspaces/{}/sessions/{}/peers"), RetryCategory.Retryable),

        // Adding sessions to a scope enqueues a scope_backfill (spec §14); a replay may enqueue a second one.
        // Spec §16: never auto-retried until the backfill enqueue is shown to be idempotent; revisit at M6.
        (Split("v3/workspaces/{}/scopes/{}/sessions"), RetryCategory.Never),

        // Non-idempotent and without Idempotency-Key support: each call mints a key, enqueues a dream, or
        // registers an endpoint.
        (Split("v3/keys"), RetryCategory.Never),
        (Split("v3/workspaces/{}/schedule_dream"), RetryCategory.Never),
        (Split("v3/workspaces/{}/webhooks"), RetryCategory.Never),

        // Grant add is a set-add (IGrantStore.AddAsync: a duplicate is a no-op), so a replay would be safe; spec §16
        // nevertheless lists it as never auto-retried (an admin write).
        (Split("v3/admin/grants"), RetryCategory.Never),
    ];

    private static readonly string[] WebhookTest = Split("v3/workspaces/{}/webhooks/test");

    private static readonly HashSet<string> ReadOnlyPostSuffixes =
        new(["list", "search", "query", "representation"], StringComparer.Ordinal);

    /// <summary>
    /// True when a transient failure of this request may be replayed.
    /// </summary>
    /// <remarks>
    /// GET, PUT and DELETE are classified by method alone, so any template with those methods is retryable (except
    /// chat and <c>GET .../webhooks/test</c>), including one the manifest does not list. Only a POST whose template
    /// matches no known route shape, or another method (PATCH, HEAD, ...), is unclassified and never retried.
    /// </remarks>
    public static bool IsRetryable(HttpMethod method, string routeTemplate, bool hasIdempotencyKey) =>
        Classify(method, routeTemplate) switch
        {
            RetryCategory.Retryable => true,
            RetryCategory.RequiresIdempotencyKey => hasIdempotencyKey,
            _ => false,
        };

    /// <summary>
    /// The route's category, or null when the method and template match no known route shape. A trailing slash
    /// (the <c>M/</c> alias) is ignored.
    /// </summary>
    public static RetryCategory? Classify(HttpMethod method, string routeTemplate)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(routeTemplate);

        var segments = Split(routeTemplate);
        if (segments.Length == 0)
        {
            return null;
        }

        if (segments[^1] == "chat")
        {
            return RetryCategory.Never;
        }

        if (method == HttpMethod.Get)
        {
            return Matches(segments, WebhookTest) ? RetryCategory.Never : RetryCategory.Retryable;
        }

        if (method == HttpMethod.Put || method == HttpMethod.Delete)
        {
            return RetryCategory.Retryable;
        }

        if (method != HttpMethod.Post)
        {
            return null;
        }

        if (ReadOnlyPostSuffixes.Contains(segments[^1]))
        {
            return RetryCategory.Retryable;
        }

        foreach (var (pattern, category) in PostRoutes)
        {
            if (Matches(segments, pattern))
            {
                return category;
            }
        }

        return null;
    }

    private static string[] Split(string template) =>
        template.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static bool Matches(string[] segments, string[] pattern)
    {
        if (segments.Length != pattern.Length)
        {
            return false;
        }

        for (var i = 0; i < pattern.Length; i++)
        {
            var matched = pattern[i] == "{}"
                ? segments[i].StartsWith('{') && segments[i].EndsWith('}')
                : string.Equals(segments[i], pattern[i], StringComparison.Ordinal);
            if (!matched)
            {
                return false;
            }
        }

        return true;
    }
}
