using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Nachos.Abstractions.Domain;

namespace Nachos.Abstractions.Work;

/// <summary>Builds work-unit keys (spec §10.1). Work in one unit is processed serially, so the key is its identity.</summary>
/// <remarks>
/// A key component is used verbatim when it is at most <see cref="MaxComponentLength"/> characters of
/// <c>A-Za-z0-9_-</c> (every name the API accepts that is short enough). Any other component becomes <c>~</c> plus the
/// first 32 hex digits of the SHA-256 of its UTF-16 code units. <c>~</c> and <c>:</c> are never verbatim, so distinct
/// names never share a key (up to a hash collision), and every key fits the 450 UTF-16 code units of the SQL key column.
/// </remarks>
public static class WorkUnitKeys
{
    /// <summary>The longest name component kept verbatim.</summary>
    public const int MaxComponentLength = 128;

    /// <summary>The longest key a store accepts (and this class builds), in UTF-16 code units.</summary>
    public const int MaxLength = 450;

    /// <summary>The singleton reconciler's unit, leased with <see cref="Stores.IWorkQueue.TryLeaseAsync"/>.</summary>
    public const string Reconciler = "reconciler:main";

    /// <summary><c>representation:{workspace}:{session}:{observed}</c>, the unit of one sender's derivations in one session.</summary>
    public static string Representation(string workspaceName, string sessionName, string observedName)
    {
        ArgumentNullException.ThrowIfNull(workspaceName);
        ArgumentNullException.ThrowIfNull(sessionName);
        ArgumentNullException.ThrowIfNull(observedName);
        return $"representation:{Component(workspaceName)}:{Component(sessionName)}:{Component(observedName)}";
    }

    /// <summary><c>summary:{workspace}:{session}</c>, the unit of one session's summaries.</summary>
    public static string Summary(string workspaceName, string sessionName)
    {
        ArgumentNullException.ThrowIfNull(workspaceName);
        ArgumentNullException.ThrowIfNull(sessionName);
        return $"summary:{Component(workspaceName)}:{Component(sessionName)}";
    }

    /// <summary>
    /// <c>deletion:{workspace}:{kind}:{name}</c> (<c>kind</c> is <c>session</c> or <c>workspace</c>), the unit of one
    /// resource's deletion job.
    /// </summary>
    /// <param name="name">The session's name, or the workspace's own name for a workspace deletion.</param>
    public static string Deletion(string workspaceName, DeletionTargetKind kind, string name)
    {
        ArgumentNullException.ThrowIfNull(workspaceName);
        ArgumentNullException.ThrowIfNull(name);
        return $"deletion:{Component(workspaceName)}:{DeletionPayload.Name(kind)}:{Component(name)}";
    }

    private static string Component(string name) =>
        name.Length is > 0 and <= MaxComponentLength && name.All(IsVerbatim)
            ? name
            : "~" + Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes(name.AsSpan())))[..32];

    private static bool IsVerbatim(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-';
}
