using System.Collections.Frozen;
using System.Security.Claims;

namespace Nachos.Api.Auth;

public sealed class NachosPrincipal : ClaimsPrincipal
{
    internal NachosPrincipal(string scheme, bool isAdmin, IEnumerable<string> workspaces,
        string? peer = null, string? session = null, bool allWorkspaces = false)
        : base(new ClaimsIdentity(scheme))
    {
        IsAdmin = isAdmin;
        Workspaces = workspaces.ToFrozenSet(StringComparer.Ordinal);
        Peer = peer;
        Session = session;
        AllWorkspaces = allWorkspaces;
    }

    public bool IsAdmin { get; }
    public IReadOnlySet<string> Workspaces { get; }
    public string? Peer { get; }
    public string? Session { get; }
    public bool AllWorkspaces { get; }
}
