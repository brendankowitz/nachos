using Microsoft.EntityFrameworkCore;
using Nachos.Abstractions;
using Nachos.DataLayer.SqlServer.Entities;

namespace Nachos.DataLayer.SqlServer.Storage;

/// <summary>
/// Exact lookups by public name. SQL Server's <c>=</c> ignores trailing spaces even under the columns' binary
/// collation, so every name match also compares <c>DATALENGTH</c>: <c>"a"</c> never finds <c>"a "</c>.
/// </summary>
internal static class Lookups
{
    public static IQueryable<WorkspaceEntity> Named(this IQueryable<WorkspaceEntity> rows, string name) =>
        rows.Where(w => w.Name == name && EF.Functions.DataLength(w.Name) == EF.Functions.DataLength(name));

    public static IQueryable<PeerEntity> Named(this IQueryable<PeerEntity> rows, long workspaceId, string name) =>
        rows.Where(p => p.WorkspaceId == workspaceId && p.Name == name && EF.Functions.DataLength(p.Name) == EF.Functions.DataLength(name));

    public static IQueryable<SessionEntity> Named(this IQueryable<SessionEntity> rows, long workspaceId, string name) =>
        rows.Where(s => s.WorkspaceId == workspaceId && s.Name == name && EF.Functions.DataLength(s.Name) == EF.Functions.DataLength(name));

    public static IQueryable<MessageEntity> Identified(this IQueryable<MessageEntity> rows, long sessionId, string publicId) =>
        rows.Where(m => m.SessionId == sessionId && m.PublicId == publicId
            && EF.Functions.DataLength(m.PublicId) == EF.Functions.DataLength(publicId));

    /// <summary>The workspace's id, or null when it does not exist.</summary>
    public static Task<long?> FindWorkspaceIdAsync(NachosDbContext db, string workspaceName, CancellationToken ct) =>
        db.Workspaces.Named(workspaceName).Select(w => (long?)w.Id).FirstOrDefaultAsync(ct);

    /// <exception cref="NotFoundException">The workspace does not exist.</exception>
    public static async Task<long> RequireWorkspaceIdAsync(NachosDbContext db, string workspaceName, CancellationToken ct) =>
        await FindWorkspaceIdAsync(db, workspaceName, ct) ?? throw WorkspaceNotFound(workspaceName);

    /// <exception cref="NotFoundException">The workspace or the session does not exist.</exception>
    public static async Task<SessionKey> RequireSessionAsync(
        NachosDbContext db, string workspaceName, string sessionName, CancellationToken ct)
    {
        var workspaceId = await RequireWorkspaceIdAsync(db, workspaceName, ct);
        var sessionId = await db.Sessions.Named(workspaceId, sessionName).Select(s => (long?)s.Id).FirstOrDefaultAsync(ct)
            ?? throw SessionNotFound(workspaceName, sessionName);
        return new SessionKey(workspaceId, sessionId);
    }

    public static NotFoundException WorkspaceNotFound(string workspaceName) => new($"Workspace '{workspaceName}' not found.");

    public static NotFoundException SessionNotFound(string workspaceName, string sessionName) =>
        new($"Session '{sessionName}' not found in workspace '{workspaceName}'.");

    public static NotFoundException PeerNotFound(string workspaceName, string peerName) =>
        new($"Peer '{peerName}' not found in workspace '{workspaceName}'.");

    /// <summary>
    /// A get-or-create lost the insert race to a row the exact lookup cannot see: the names differ only by trailing
    /// spaces, which the unique constraint treats as equal. Unreachable through the API, whose ids contain no spaces.
    /// </summary>
    public static NachosValidationException TrailingSpaceCollision(string kind, string name) =>
        new($"The {kind} name '{name}' differs from an existing name only by trailing spaces, which this provider cannot store.");
}

/// <summary>The surrogate keys of an existing session.</summary>
internal readonly record struct SessionKey(long WorkspaceId, long SessionId);
