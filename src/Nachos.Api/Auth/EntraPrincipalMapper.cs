using System.Security.Claims;
using Nachos.Abstractions;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Stores;

namespace Nachos.Api.Auth;

internal static class EntraPrincipalMapper
{
    internal static async Task<NachosPrincipal> MapAsync(ClaimsPrincipal principal, IGrantStore store, CancellationToken ct)
    {
        var roles = principal.FindAll("roles").Select(claim => claim.Value);
        if (roles.Contains(GrantRoles.Admin, StringComparer.Ordinal))
            return new("Entra", true, []);
        var oid = principal.FindFirst("oid")?.Value;
        if (string.IsNullOrWhiteSpace(oid) || !roles.Contains(GrantRoles.Workspace, StringComparer.Ordinal))
            throw new AuthException("Invalid Entra authority.");
        var grants = await store.GetWorkspaceGrantsAsync(oid, ct);
        return new("Entra", false, grants.Workspaces, allWorkspaces: grants.AllWorkspaces);
    }
}
