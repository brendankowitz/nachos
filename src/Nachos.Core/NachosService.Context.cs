using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Core.Context;
using Nachos.Core.Memory;
using Nachos.Core.Validation;

namespace Nachos.Core;

/// <summary>The M3 session context and summaries routes (<c>GET S/context</c>, <c>GET S/summaries</c>).</summary>
/// <remarks>
/// These are not yet on <see cref="INachosClient"/>: the interface is one method per wire route, implemented over HTTP
/// too, and the routes and the HTTP client arrive together in M3 Task 9. The route also owns the auth rule that a
/// peer-scoped key must be an active member of every session named in <c>sessions</c>.
/// </remarks>
public sealed partial class NachosService
{
    /// <summary>
    /// The session's context within <see cref="SessionContextQuery.Tokens"/> content tokens (see
    /// <see cref="SessionContextService"/>). <c>limit_to_session</c> and <c>sessions</c> together form the visibility
    /// allowlist; when they do not include this session, its messages and summary are hidden too.
    /// </summary>
    /// <exception cref="RequestValidationException">A bound is exceeded or the parameters do not combine.</exception>
    /// <exception cref="NachosValidationException">An id is invalid, or <c>scope</c> is set; scopes are not supported in this version.</exception>
    /// <exception cref="NotFoundException">The workspace or the session does not exist.</exception>
    public async Task<SessionContext> GetSessionContextAsync(
        string workspaceId, string sessionId, SessionContextQuery query, CancellationToken ct = default)
    {
        Validate(ct, (workspaceId, "workspace_id"), (sessionId, "session_id"));
        ArgumentNullException.ThrowIfNull(query);
        var tokens = ReadContextTokens(query.Tokens);
        ValidateOptional(query.PeerTarget, "peer_target");
        ValidateOptional(query.PeerPerspective, "peer_perspective");
        foreach (var session in query.Sessions ?? [])
        {
            IdValidator.Validate(session, "sessions");
        }

        if (query.PeerPerspective is not null && query.PeerTarget is null)
        {
            throw Invalid(["query", "peer_perspective"], "peer_perspective requires peer_target", "value_error");
        }

        if (query.Scope is not null && query.PeerTarget is null)
        {
            throw Invalid(["query", "scope"], "scope requires peer_target", "value_error");
        }

        if (query.Scope is not null && query.PeerPerspective is not null)
        {
            throw Invalid(["query", "scope"], "scope cannot be combined with peer_perspective", "value_error");
        }

        var representationQuery = ReadRepresentationQuery("query", query.MaxConclusions, query.IncludeMostFrequent,
            query.SearchQuery, query.SearchTopK, query.SearchMaxDistance, filter: null);
        // Any scope is rejected by the policy, so which id it named does not matter.
        var policy = VisibilityPolicy.Create(ContextAllowlist(sessionId, query), query.Scope is null ? null : []);
        var representation = query.PeerTarget is null
            ? null
            : new ContextRepresentation(query.PeerPerspective ?? query.PeerTarget, query.PeerTarget, representationQuery);

        var context = await sessionContext.GetContextAsync(workspaceId, sessionId,
            new SessionContextRequest(tokens, query.Summary ?? true, representation), policy, ct).ConfigureAwait(false);
        return new(sessionId, [.. context.Messages.Select(Map)], context.Summary is null ? null : Map(context.Summary),
            context.Representation, context.PeerCard);
    }

    /// <summary>The session's current short and long summaries; each is null until the summarizer has made one.</summary>
    /// <exception cref="NotFoundException">The workspace or the session does not exist.</exception>
    public async Task<SessionSummaries> GetSessionSummariesAsync(string workspaceId, string sessionId, CancellationToken ct = default)
    {
        Validate(ct, (workspaceId, "workspace_id"), (sessionId, "session_id"));
        var summaries = await sessionContext.GetSummariesAsync(workspaceId, sessionId, VisibilityPolicy.Unrestricted, ct)
            .ConfigureAwait(false);
        return new(sessionId, Find(SummaryKind.Short), Find(SummaryKind.Long));

        Summary? Find(SummaryKind kind) => summaries.FirstOrDefault(s => s.Kind == kind) is { } found ? Map(found) : null;
    }

    private static int ReadContextTokens(int? tokens) => tokens switch
    {
        null => ContextBudget.MaxTokens,
        < 1 => throw Invalid(["query", "tokens"], "Input should be greater than or equal to 1", "greater_than_equal"),
        > ContextBudget.MaxTokens => throw Invalid(["query", "tokens"],
            $"Input should be less than or equal to {ContextBudget.MaxTokens}", "less_than_equal"),
        _ => tokens.Value,
    };

    private static List<string>? ContextAllowlist(string sessionId, SessionContextQuery query)
    {
        if (query.LimitToSession != true && query.Sessions is null)
        {
            return null;
        }

        var allowlist = new List<string>(query.Sessions ?? []);
        if (query.LimitToSession == true)
        {
            allowlist.Add(sessionId);
        }

        return allowlist;
    }

    private static Summary Map(SummaryRecord row) => new(row.Content, row.CoversThroughMessageId, row.Kind switch
    {
        SummaryKind.Short => "short",
        SummaryKind.Long => "long",
        _ => throw new InvalidOperationException($"Unknown summary kind {row.Kind}."),
    }, row.CreatedAt, row.TokenCount);
}
