using System.Collections.Frozen;

namespace Nachos.Api.Auth;

public static class MemberReadRoutes
{
    private const string Session = "/v3/workspaces/{workspace_id}/sessions/{session_id}";
    public static IReadOnlySet<string> All { get; } = new[]
    {
        "GET " + Session + "/peers",
        "GET " + Session + "/peers/{peer_id}/config",
        "POST " + Session + "/messages/list",
        "GET " + Session + "/messages/{message_id}",
    }.ToFrozenSet(StringComparer.Ordinal);
}
