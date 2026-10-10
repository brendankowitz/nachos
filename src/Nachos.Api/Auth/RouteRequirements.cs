using Microsoft.AspNetCore.Authorization;

namespace Nachos.Api.Auth;

internal sealed class RouteRequirements : IAuthorizationRequirement
{
    private const string W = "/v3/workspaces/{workspace_id}";
    private const string P = W + "/peers/{peer_id}";
    private const string S = W + "/sessions/{session_id}";
    internal static RouteAccess For(string method, string template) => (method, template) switch
    {
        ("POST", "/v3/workspaces") => RouteAccess.WorkspaceBody,
        ("PUT", W) or ("POST", W + "/peers") or ("POST", W + "/peers/list")
            or ("POST", W + "/sessions") or ("POST", W + "/sessions/list") => RouteAccess.Workspace,
        ("PUT", P) or ("POST", P + "/sessions") => RouteAccess.Peer,
        ("PUT", S) or ("POST" or "PUT" or "DELETE" or "GET", S + "/peers")
            or ("GET" or "PUT", S + "/peers/{peer_id}/config")
            or ("POST", S + "/messages") or ("POST", S + "/messages/list")
            or ("GET" or "PUT", S + "/messages/{message_id}") => RouteAccess.Session,
        _ => RouteAccess.Admin,
    };
}

internal enum RouteAccess { Admin, WorkspaceBody, Workspace, Peer, Session }
