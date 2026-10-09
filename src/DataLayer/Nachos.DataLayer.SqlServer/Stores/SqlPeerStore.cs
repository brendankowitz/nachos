using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Abstractions.Stores;
using Nachos.DataLayer.SqlServer.Entities;
using Nachos.DataLayer.SqlServer.Filtering;
using Nachos.DataLayer.SqlServer.Storage;

namespace Nachos.DataLayer.SqlServer.Stores;

internal sealed class SqlPeerStore(SqlStoreRuntime runtime) : IPeerStore
{
    public async Task<PeerRecord> GetOrCreateAsync(
        string workspaceName,
        string name,
        JsonObject? metadata,
        JsonObject? configuration,
        CancellationToken ct,
        bool isInternal = false)
    {
        ReentryGuard.ThrowIfReentered();
        var storedMetadata = SqlJson.ToStorage(metadata, JsonField.Metadata);
        var storedConfiguration = SqlJson.ToStorage(configuration, JsonField.Configuration);
        await using var db = await runtime.OpenAsync(ct);

        var workspaceId = await Lookups.RequireWorkspaceIdAsync(db, workspaceName, ct);
        var peer = await Upserts.EnsurePeerAsync(
            db, workspaceId, name, storedMetadata, storedConfiguration, isInternal, runtime.Clock.GetUtcNow(), ct);
        return peer.ToRecord(workspaceName);
    }

    public async Task<PeerRecord?> GetAsync(string workspaceName, string name, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        await using var db = await runtime.OpenAsync(ct);

        if (await Lookups.FindWorkspaceIdAsync(db, workspaceName, ct) is not { } workspaceId)
        {
            return null;
        }

        return (await db.Peers.AsNoTracking().Named(workspaceId, name).FirstOrDefaultAsync(ct))?.ToRecord(workspaceName);
    }

    public async Task<PeerRecord> UpdateAsync(
        string workspaceName, string name, JsonObject? metadata, JsonObject? configuration, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        var storedMetadata = SqlJson.ToStorageOptional(metadata, JsonField.Metadata);
        var storedConfiguration = SqlJson.ToStorageOptional(configuration, JsonField.Configuration);
        await using var db = await runtime.OpenAsync(ct);

        var workspaceId = await Lookups.RequireWorkspaceIdAsync(db, workspaceName, ct);
        var updated = await JsonUpdate.ApplyAsync(
            db.Peers,
            "dbo.Peers",
            "WorkspaceId = @workspace AND Name = @name AND DATALENGTH(Name) = DATALENGTH(@name)",
            storedMetadata,
            storedConfiguration,
            [SqlParameters.Long("@workspace", workspaceId), SqlParameters.Text("@name", name)],
            ct);
        return (updated ?? await db.Peers.AsNoTracking().Named(workspaceId, name).FirstOrDefaultAsync(ct)
            ?? throw Lookups.PeerNotFound(workspaceName, name)).ToRecord(workspaceName);
    }

    public async Task<Page<PeerRecord>> ListAsync(
        string workspaceName, PeerKind kind, FilterNode? filter, PageRequest page, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        var (where, parameters) = SqlFilterCompiler.Compile(filter, ResourceKind.Peer, "t");
        await using var db = await runtime.OpenAsync(ct);

        var workspaceId = await Lookups.RequireWorkspaceIdAsync(db, workspaceName, ct);
        var kindClause = kind switch
        {
            PeerKind.Regular => " AND t.IsInternal = 0",
            PeerKind.Scope => " AND t.IsInternal = 1",
            _ => string.Empty,
        };
        var rows = db.Peers
            .FromSqlRaw(
                string.Concat("SELECT * FROM dbo.Peers AS t WHERE t.WorkspaceId = @workspace", kindClause, " AND ", where),
                [SqlParameters.Long("@workspace", workspaceId), .. parameters])
            .AsNoTracking();
        return await Paging.ToPageAsync(rows, page, InCreationOrder, peer => peer.ToRecord(workspaceName), ct);
    }

    public async Task<Page<SessionRecord>> ListSessionsForPeerAsync(
        string workspaceName, string peerName, FilterNode? filter, PageRequest page, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        var (where, parameters) = SqlFilterCompiler.Compile(filter, ResourceKind.Session, "t");
        await using var db = await runtime.OpenAsync(ct);

        var workspaceId = await Lookups.RequireWorkspaceIdAsync(db, workspaceName, ct);
        var peerId = await db.Peers.Named(workspaceId, peerName).Select(p => (long?)p.Id).FirstOrDefaultAsync(ct)
            ?? throw Lookups.PeerNotFound(workspaceName, peerName);
        var rows = db.Sessions
            .FromSqlRaw(
                string.Concat(
                    "SELECT * FROM dbo.Sessions AS t WHERE t.WorkspaceId = @workspace AND EXISTS (SELECT 1 FROM dbo.SessionPeers AS m WHERE m.WorkspaceId = t.WorkspaceId AND m.SessionId = t.Id AND m.PeerId = @peer AND m.LeftAt IS NULL) AND ",
                    where),
                [SqlParameters.Long("@workspace", workspaceId), SqlParameters.Long("@peer", peerId), .. parameters])
            .AsNoTracking();
        return await Paging.ToPageAsync(rows, page, SqlSessionStore.InCreationOrder, session => session.ToRecord(workspaceName), ct);
    }

    internal static IQueryable<PeerEntity> InCreationOrder(IQueryable<PeerEntity> peers, bool reverse) =>
        reverse
            ? peers.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
            : peers.OrderBy(p => p.CreatedAt).ThenBy(p => p.Id);
}
