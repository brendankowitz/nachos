using System.Text.Json.Nodes;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Core.Memory;

namespace Nachos.Core;

/// <summary>
/// The M3 message search routes (<c>W/search</c>, <c>S/search</c>, <c>P/search</c>). A session or peer that does not
/// exist matches nothing; a missing workspace is a 404, except that a blank query returns no messages without reading.
/// </summary>
/// <remarks>
/// These are not yet on <see cref="INachosClient"/>: the interface is one method per wire route, implemented over HTTP
/// too, and the routes and the HTTP client arrive together in M3 Task 9.
/// </remarks>
public sealed partial class NachosService
{
    /// <summary>Searches every session of the workspace.</summary>
    /// <exception cref="RequestValidationException"><c>query</c> is missing or <c>limit</c> is out of bounds.</exception>
    /// <exception cref="NachosValidationException"><c>scope</c> is set; scopes are not supported in this version.</exception>
    public Task<IReadOnlyList<Message>> SearchWorkspaceAsync(
        string workspaceId, WorkspaceMessageSearchOptions options, CancellationToken ct = default)
    {
        Validate(ct, (workspaceId, "workspace_id"));
        ArgumentNullException.ThrowIfNull(options);
        // Any scope is rejected by the policy, so which id it named does not matter.
        var policy = VisibilityPolicy.Create(null, options.Scope is null ? null : []);
        return SearchAsync(workspaceId, new MessageSearchScope.Workspace(), options.Query, options.Limit, options.Filters,
            policy, ct);
    }

    /// <summary>Searches one session's messages.</summary>
    /// <exception cref="RequestValidationException"><c>query</c> is missing or <c>limit</c> is out of bounds.</exception>
    public Task<IReadOnlyList<Message>> SearchSessionAsync(
        string workspaceId, string sessionId, MessageSearchOptions options, CancellationToken ct = default)
    {
        Validate(ct, (workspaceId, "workspace_id"), (sessionId, "session_id"));
        ArgumentNullException.ThrowIfNull(options);
        return SearchAsync(workspaceId, new MessageSearchScope.Session(sessionId), options.Query, options.Limit,
            options.Filters, VisibilityPolicy.Create([sessionId], null), ct);
    }

    /// <summary>
    /// Searches the messages the peer could have seen: those of its sessions sent while it was a member
    /// (<see cref="MessageSearchScope.PeerPerspective"/>).
    /// </summary>
    /// <exception cref="RequestValidationException"><c>query</c> is missing or <c>limit</c> is out of bounds.</exception>
    public Task<IReadOnlyList<Message>> SearchPeerAsync(
        string workspaceId, string peerId, MessageSearchOptions options, CancellationToken ct = default)
    {
        Validate(ct, (workspaceId, "workspace_id"), (peerId, "peer_id"));
        ArgumentNullException.ThrowIfNull(options);
        return SearchAsync(workspaceId, new MessageSearchScope.PeerPerspective(peerId), options.Query, options.Limit,
            options.Filters, VisibilityPolicy.Unrestricted, ct);
    }

    private async Task<IReadOnlyList<Message>> SearchAsync(string workspaceId, MessageSearchScope scope, string? query,
        int limit, JsonObject? filters, VisibilityPolicy policy, CancellationToken ct)
    {
        if (query is null)
        {
            throw Invalid(["body", "query"], "Input should be a valid string", "string_type");
        }

        CheckCount(limit, "body", "limit");
        var filter = FilterParser.Parse(filters, ResourceKind.Message);
        var found = await search.SearchAsync(new MessageSearchRequest(workspaceId, scope, filter), query, limit, policy, ct)
            .ConfigureAwait(false);
        return [.. found.Select(Map)];
    }
}
