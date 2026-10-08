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

        // Membership adds: repeating one leaves the same members.
        (Split("v3/workspaces/{}/sessions/{}/peers"), RetryCategory.Retryable),
        (Split("v3/workspaces/{}/scopes/{}/sessions"), RetryCategory.Retryable),

        // Non-idempotent and without Idempotency-Key support: each call mints a key, enqueues a dream, registers
        // an endpoint, or writes a grant.
        (Split("v3/keys"), RetryCategory.Never),
        (Split("v3/workspaces/{}/schedule_dream"), RetryCategory.Never),
        (Split("v3/workspaces/{}/webhooks"), RetryCategory.Never),
        (Split("v3/admin/grants"), RetryCategory.Never),
    ];

    private static readonly string[] WebhookTest = Split("v3/workspaces/{}/webhooks/test");

    private static readonly HashSet<string> ReadOnlyPostSuffixes =
        new(["list", "search", "query", "representation"], StringComparer.Ordinal);

    /// <summary>
    /// True when a transient failure of this request may be replayed. Unknown routes are never retried.
    /// </summary>
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
