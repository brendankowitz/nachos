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
        if (principal.Session is not null && principal.Session == session)
        {
            context.Succeed(requirement);
            return;
        }
        if (principal.Peer is null || session is null ||
            !MemberReadRoutes.All.Contains(http.Request.Method + " " + template)) return;
        if (template.EndsWith("/config", StringComparison.Ordinal) &&
            principal.Peer != http.Request.RouteValues["peer_id"] as string) return;
        try
        {
            if (await store.Sessions.IsActiveMemberAsync(workspace, session, principal.Peer, http.RequestAborted))
                context.Succeed(requirement);
        }
        catch (NotFoundException)
        {
            // A missing session cannot grant membership; operational store failures must propagate.
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
