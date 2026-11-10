using Microsoft.AspNetCore.Authorization;

namespace Nachos.Api.Auth;

/// <remarks>
/// The M2 memory rows follow Honcho's public scoped-key documentation (spec §9.2): the peer-card, representation and
/// context routes are peer routes ("a peer-scoped key acts on its own peer"; "a session-scoped key ... cannot reach peer
/// routes"). The docs are silent on conclusions and queue status, which take the workspace fallback.
/// <para>
/// The M3 rows: <c>S/context</c>, <c>S/summaries</c> and <c>S/search</c> are session routes that a member's peer-scoped
/// key may read (documented: "read-only access to the sessions its peer is an active member of (context, summaries,
/// peers, its own per-session config, search, and message reads)"; see <see cref="MemberReadRoutes"/>); <c>P/search</c>
/// is a peer route ("acts on its own peer"). The docs are silent on <c>W/search</c>, <c>W/jobs</c> and both deletes,
/// which take the workspace fallback.
/// </para>
/// </remarks>
internal sealed class RouteRequirements : IAuthorizationRequirement
{
    private const string W = "/v3/workspaces/{workspace_id}";
    private const string P = W + "/peers/{peer_id}";
    private const string S = W + "/sessions/{session_id}";
    private const string C = W + "/conclusions";
    internal static RouteAccess For(string method, string template) => (method, template) switch
    {
        ("POST", "/v3/workspaces") => RouteAccess.WorkspaceBody,
        ("PUT", W) or ("POST", W + "/peers") or ("POST", W + "/peers/list")
            or ("POST", W + "/sessions") or ("POST", W + "/sessions/list") => RouteAccess.Workspace,
        ("POST", C) or ("POST", C + "/list") or ("POST", C + "/query")
            or ("GET" or "DELETE", C + "/{conclusion_id}") or ("GET", W + "/queue/status") => RouteAccess.Workspace,
        ("DELETE", W) or ("POST", W + "/search") or ("GET", W + "/jobs") or ("DELETE", S) => RouteAccess.Workspace,
        ("PUT", P) or ("POST", P + "/sessions") => RouteAccess.Peer,
        ("GET" or "PUT", P + "/card") or ("GET", P + "/context") or ("POST", P + "/representation")
            or ("POST", P + "/search") => RouteAccess.Peer,
        ("PUT", S) or ("POST" or "PUT" or "DELETE" or "GET", S + "/peers")
            or ("GET" or "PUT", S + "/peers/{peer_id}/config")
            or ("POST", S + "/messages") or ("POST", S + "/messages/list")
            or ("GET" or "PUT", S + "/messages/{message_id}")
            or ("GET", S + "/context") or ("GET", S + "/summaries") or ("POST", S + "/search") => RouteAccess.Session,
        _ => RouteAccess.Admin,
    };
}

internal enum RouteAccess { Admin, WorkspaceBody, Workspace, Peer, Session }
