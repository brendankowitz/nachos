using Microsoft.AspNetCore.Authorization;
using Nachos.Abstractions;
using Nachos.Abstractions.Stores;
using Nachos.Api.Json;

namespace Nachos.Api.Auth;

internal sealed class NachosAuthorizationHandler(IMemoryStore store, IConfiguration configuration)
    : AuthorizationHandler<RouteRequirements>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, RouteRequirements requirement)
    {
        if (!configuration.GetValue("Nachos:Auth:Enabled", true))
        {
            context.Succeed(requirement);
            return;
        }
        if (context.User is not NachosPrincipal principal || context.Resource is not HttpContext http) return;
        if (principal.IsAdmin) { context.Succeed(requirement); return; }
        if (http.GetEndpoint() is not RouteEndpoint route) return;
        var template = route.RoutePattern.RawText!;
        var access = RouteRequirements.For(http.Request.Method, template);
        if (access == RouteAccess.Admin) return;
        if (access == RouteAccess.WorkspaceBody && (principal.Peer is not null || principal.Session is not null)) return;
        var workspace = access == RouteAccess.WorkspaceBody
            ? await ReadWorkspaceAsync(http) : http.Request.RouteValues["workspace_id"] as string;
        if (workspace is null || (!principal.AllWorkspaces && !principal.Workspaces.Contains(workspace))) return;
        if (principal.Peer is null && principal.Session is null) { context.Succeed(requirement); return; }
        if (access == RouteAccess.Peer && principal.Peer is not null &&
            principal.Peer == http.Request.RouteValues["peer_id"] as string)
        {
            context.Succeed(requirement);
            return;
        }
        if (access != RouteAccess.Session) return;
        var session = http.Request.RouteValues["session_id"] as string;
        var isContext = template == SessionContextTemplate;
        if (principal.Session is not null && principal.Session == session)
        {
            if (!isContext || SessionKeyMayReadContext(http.Request, session)) context.Succeed(requirement);
            return;
        }
        if (principal.Peer is null || session is null ||
            !MemberReadRoutes.All.Contains(http.Request.Method + " " + template)) return;
        if (template.EndsWith("/config", StringComparison.Ordinal) &&
            principal.Peer != http.Request.RouteValues["peer_id"] as string) return;
        if (!await IsActiveMemberAsync(workspace, session, principal.Peer, http.RequestAborted)) return;
        if (!isContext || await PeerKeyMayReadContextAsync(http.Request, workspace, session, principal.Peer))
            context.Succeed(requirement);
    }

    private const string SessionContextTemplate = "/v3/workspaces/{workspace_id}/sessions/{session_id}/context";

    // S/context reads beyond its own session: "sessions" widens the representation's allowlist, the perspective chooses
    // whose view is read, and "scope" picks a scope's view. Honcho's docs: a scope "requires a workspace- or admin-level
    // key", "a peer-scoped key must be an active member of every session named", a peer-scoped key "cannot ... act on
    // other peers", and "a session-scoped key is confined to its own session". Workspace and admin keys pass as given.
    // Until M6, NachosService reads with an unrestricted policy, so this is the only gate for scoped keys.
    private static bool SessionKeyMayReadContext(HttpRequest request, string session) =>
        QueryParameters.Text(request, "scope") is null &&
        (QueryParameters.Texts(request, "sessions") ?? []).All(named => named == session);

    private async Task<bool> PeerKeyMayReadContextAsync(HttpRequest request, string workspace, string session, string peer)
    {
        if (QueryParameters.Text(request, "scope") is not null) return false;
        var observer = QueryParameters.Text(request, "peer_perspective") ?? QueryParameters.Text(request, "peer_target");
        if (observer is not null && observer != peer) return false;
        foreach (var named in (QueryParameters.Texts(request, "sessions") ?? []).Distinct(StringComparer.Ordinal))
        {
            if (named != session && !await IsActiveMemberAsync(workspace, named, peer, request.HttpContext.RequestAborted))
                return false;
        }
        return true;
    }

    private async Task<bool> IsActiveMemberAsync(string workspace, string session, string peer, CancellationToken ct)
    {
        try
        {
            return await store.Sessions.IsActiveMemberAsync(workspace, session, peer, ct);
        }
        catch (NotFoundException)
        {
            // A missing session cannot grant membership; operational store failures must propagate.
            return false;
        }
    }

    private static async Task<string> ReadWorkspaceAsync(HttpContext http)
    {
        var buffer = new MemoryStream();
        http.Response.RegisterForDispose(buffer);
        await http.Request.Body.CopyToAsync(buffer, http.RequestAborted);
        http.Request.Body = buffer;
        buffer.Position = 0;
        try
        {
            using var body = await RequestBody.ReadAsync(http.Request);
            return body.RequiredId();
        }
        finally { buffer.Position = 0; }
    }
}
