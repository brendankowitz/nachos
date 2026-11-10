using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Nachos.Core;

/// <summary>
/// Derives the wire <c>uid</c> of a workspace, peer or session: 32 lowercase hex characters (128 bits of SHA-256)
/// over the resource kind, its workspace, its name and its creation instant.
/// </summary>
/// <remarks>
/// None of those inputs ever changes for a stored resource, so the uid is stable and immutable without a stored column,
/// and both providers produce the same value. A resource deleted and created again under the same name gets a new
/// creation instant and therefore a new uid. Names are restricted to <c>[A-Za-z0-9_-]</c>, so the NUL separator
/// cannot be forged by a name.
/// </remarks>
public static class ResourceUid
{
    public static string ForWorkspace(string name, DateTimeOffset createdAt) => Of("workspace", name, name, createdAt);

    public static string ForPeer(string workspaceName, string name, DateTimeOffset createdAt) =>
        Of("peer", workspaceName, name, createdAt);

    public static string ForSession(string workspaceName, string name, DateTimeOffset createdAt) =>
        Of("session", workspaceName, name, createdAt);

    private static string Of(string kind, string workspaceName, string name, DateTimeOffset createdAt)
    {
        var input = string.Join('\0', kind, workspaceName, name, createdAt.UtcTicks.ToString(CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }
}
