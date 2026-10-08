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

/// <remarks>Membership changes follow the lock order documented on <see cref="Upserts"/>.</remarks>
internal sealed class SqlSessionStore(SqlStoreRuntime runtime) : ISessionStore
{
    public async Task<SessionRecord> GetOrCreateAsync(
        string workspaceName,
        string name,
        JsonObject? metadata,
        JsonObject? configuration,
        IReadOnlyDictionary<string, SessionPeerConfig>? peers,
        CancellationToken ct)
    {
        runtime.Guard.ThrowIfReentered();
        var storedMetadata = SqlJson.ToStorage(metadata, "metadata");
        var storedConfiguration = SqlJson.ToStorage(configuration, "configuration");
        await using var db = await runtime.OpenAsync(ct);

        var workspaceId = await Lookups.RequireWorkspaceIdAsync(db, workspaceName, ct);
        var now = runtime.Clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var session = await EnsureSessionAsync(db, workspaceId, name, storedMetadata, storedConfiguration, now, ct);
        if (peers is { Count: > 0 })
        {
            await Upserts.LockSessionAsync(db, session.Id, ct);
            await Upserts.ActivateMembersAsync(db, new SessionKey(workspaceId, session.Id), peers, now, ct);
        }

        await transaction.CommitAsync(ct);
        return session.ToRecord(workspaceName);
    }

    public async Task<SessionRecord?> GetAsync(string workspaceName, string name, CancellationToken ct)
    {
        runtime.Guard.ThrowIfReentered();
        await using var db = await runtime.OpenAsync(ct);

        if (await Lookups.FindWorkspaceIdAsync(db, workspaceName, ct) is not { } workspaceId)
        {
            return null;
        }

        return (await db.Sessions.AsNoTracking().Named(workspaceId, name).FirstOrDefaultAsync(ct))?.ToRecord(workspaceName);
    }

    public async Task<SessionRecord> UpdateAsync(
        string workspaceName, string name, JsonObject? metadata, JsonObject? configuration, CancellationToken ct)
    {
        runtime.Guard.ThrowIfReentered();
        var storedMetadata = SqlJson.ToStorageOptional(metadata, "metadata");
        var storedConfiguration = SqlJson.ToStorageOptional(configuration, "configuration");
        await using var db = await runtime.OpenAsync(ct);

        var workspaceId = await Lookups.RequireWorkspaceIdAsync(db, workspaceName, ct);
        var updated = await JsonUpdate.ApplyAsync(
            db.Sessions,
            "dbo.Sessions",
            "WorkspaceId = @workspace AND Name = @name AND DATALENGTH(Name) = DATALENGTH(@name)",
            storedMetadata,
            storedConfiguration,
            [SqlParameters.Long("@workspace", workspaceId), SqlParameters.Text("@name", name)],
            ct);
        return (updated ?? await db.Sessions.AsNoTracking().Named(workspaceId, name).FirstOrDefaultAsync(ct)
            ?? throw Lookups.SessionNotFound(workspaceName, name)).ToRecord(workspaceName);
    }

    public async Task<Page<SessionRecord>> ListAsync(
        string workspaceName, FilterNode? filter, PageRequest page, CancellationToken ct)
    {
        runtime.Guard.ThrowIfReentered();
        var (where, parameters) = SqlFilterCompiler.Compile(filter, ResourceKind.Session, "t");
        await using var db = await runtime.OpenAsync(ct);

        var workspaceId = await Lookups.RequireWorkspaceIdAsync(db, workspaceName, ct);
        var rows = db.Sessions
            .FromSqlRaw(
                string.Concat("SELECT * FROM dbo.Sessions AS t WHERE t.WorkspaceId = @workspace AND ", where),
                [SqlParameters.Long("@workspace", workspaceId), .. parameters])
            .AsNoTracking();
        return await Paging.ToPageAsync(rows, page, InCreationOrder, session => session.ToRecord(workspaceName), ct);
    }

    public async Task AddPeersAsync(
        string workspaceName,
        string sessionName,
        IReadOnlyDictionary<string, SessionPeerConfig> peers,
        CancellationToken ct)
    {
        runtime.Guard.ThrowIfReentered();
        await using var db = await runtime.OpenAsync(ct);

        var session = await Lookups.RequireSessionAsync(db, workspaceName, sessionName, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await Upserts.LockSessionAsync(db, session.SessionId, ct);
        await Upserts.ActivateMembersAsync(db, session, peers, runtime.Clock.GetUtcNow(), ct);
        await transaction.CommitAsync(ct);
    }

    public async Task SetPeersAsync(
        string workspaceName,
        string sessionName,
        IReadOnlyDictionary<string, SessionPeerConfig> peers,
        CancellationToken ct)
    {
        runtime.Guard.ThrowIfReentered();
        await using var db = await runtime.OpenAsync(ct);

        var session = await Lookups.RequireSessionAsync(db, workspaceName, sessionName, ct);
        var now = runtime.Clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await Upserts.LockSessionAsync(db, session.SessionId, ct);
        await Upserts.ActivateMembersAsync(db, session, peers, now, ct);

        // Every listed peer is now an active member, so "not listed" is "not among the given names".
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE m SET LeftAt = @now
            FROM dbo.SessionPeers AS m
            JOIN dbo.Peers AS p ON p.WorkspaceId = m.WorkspaceId AND p.Id = m.PeerId
            WHERE m.WorkspaceId = @workspace AND m.SessionId = @session AND m.LeftAt IS NULL
              AND NOT EXISTS (
                SELECT 1 FROM OPENJSON(@names) AS n
                WHERE n.[value] COLLATE Latin1_General_100_BIN2 = p.Name AND DATALENGTH(n.[value]) = DATALENGTH(p.Name))
            """,
            [
                SqlParameters.Time("@now", now),
                SqlParameters.Long("@workspace", session.WorkspaceId),
                SqlParameters.Long("@session", session.SessionId),
                SqlParameters.LongText("@names", new JsonArray([.. peers.Keys.Select(key => (JsonNode)key)]).ToJsonString()),
            ],
            ct);
        await transaction.CommitAsync(ct);
    }

    public async Task RemovePeersAsync(
        string workspaceName, string sessionName, IReadOnlyList<string> peerNames, CancellationToken ct)
    {
        runtime.Guard.ThrowIfReentered();
        await using var db = await runtime.OpenAsync(ct);

        var session = await Lookups.RequireSessionAsync(db, workspaceName, sessionName, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await Upserts.LockSessionAsync(db, session.SessionId, ct);
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE m SET LeftAt = @now
            FROM dbo.SessionPeers AS m
            JOIN dbo.Peers AS p ON p.WorkspaceId = m.WorkspaceId AND p.Id = m.PeerId
            WHERE m.WorkspaceId = @workspace AND m.SessionId = @session AND m.LeftAt IS NULL
              AND EXISTS (
                SELECT 1 FROM OPENJSON(@names) AS n
                WHERE n.[value] COLLATE Latin1_General_100_BIN2 = p.Name AND DATALENGTH(n.[value]) = DATALENGTH(p.Name))
            """,
            [
                SqlParameters.Time("@now", runtime.Clock.GetUtcNow()),
                SqlParameters.Long("@workspace", session.WorkspaceId),
                SqlParameters.Long("@session", session.SessionId),
                SqlParameters.LongText("@names", new JsonArray([.. peerNames.Select(name => (JsonNode)name)]).ToJsonString()),
            ],
            ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<Page<PeerRecord>> ListPeersAsync(
        string workspaceName, string sessionName, PageRequest page, CancellationToken ct)
    {
        runtime.Guard.ThrowIfReentered();
        await using var db = await runtime.OpenAsync(ct);

        var session = await Lookups.RequireSessionAsync(db, workspaceName, sessionName, ct);
        var rows = db.Peers.AsNoTracking().Where(peer => db.SessionPeers.Any(member =>
            member.WorkspaceId == session.WorkspaceId
            && member.SessionId == session.SessionId
            && member.PeerId == peer.Id
            && member.LeftAt == null));
        return await Paging.ToPageAsync(rows, page, SqlPeerStore.InCreationOrder, peer => peer.ToRecord(workspaceName), ct);
    }

    public async Task<SessionPeerConfig> GetPeerConfigAsync(
        string workspaceName, string sessionName, string peerName, CancellationToken ct)
    {
        runtime.Guard.ThrowIfReentered();
        await using var db = await runtime.OpenAsync(ct);

        var session = await Lookups.RequireSessionAsync(db, workspaceName, sessionName, ct);
        var stored = await ActiveMember(db, session, peerName).Select(member => member.Configuration).FirstOrDefaultAsync(ct)
            ?? throw NotAnActiveMember(sessionName, peerName);
        return Records.DeserializePeerConfig(stored);
    }

    public async Task SetPeerConfigAsync(
        string workspaceName, string sessionName, string peerName, SessionPeerConfig config, CancellationToken ct)
    {
        runtime.Guard.ThrowIfReentered();
        await using var db = await runtime.OpenAsync(ct);

        var session = await Lookups.RequireSessionAsync(db, workspaceName, sessionName, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await Upserts.LockSessionAsync(db, session.SessionId, ct);
        var updated = await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE m SET Configuration = @config
            FROM dbo.SessionPeers AS m
            JOIN dbo.Peers AS p ON p.WorkspaceId = m.WorkspaceId AND p.Id = m.PeerId
            WHERE m.WorkspaceId = @workspace AND m.SessionId = @session AND m.LeftAt IS NULL
              AND p.Name = @peer AND DATALENGTH(p.Name) = DATALENGTH(@peer)
            """,
            [
                SqlParameters.LongText("@config", Records.Serialize(config)),
                SqlParameters.Long("@workspace", session.WorkspaceId),
                SqlParameters.Long("@session", session.SessionId),
                SqlParameters.Text("@peer", peerName),
            ],
            ct);
        if (updated == 0)
        {
            throw NotAnActiveMember(sessionName, peerName);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task<bool> IsActiveMemberAsync(
        string workspaceName, string sessionName, string peerName, CancellationToken ct)
    {
        runtime.Guard.ThrowIfReentered();
        await using var db = await runtime.OpenAsync(ct);

        var session = await Lookups.RequireSessionAsync(db, workspaceName, sessionName, ct);
        return await ActiveMember(db, session, peerName).AnyAsync(ct);
    }

    internal static IQueryable<SessionEntity> InCreationOrder(IQueryable<SessionEntity> sessions, bool reverse) =>
        reverse
            ? sessions.OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id)
            : sessions.OrderBy(s => s.CreatedAt).ThenBy(s => s.Id);

    /// <summary>Returns the session, inserting it when missing (see <see cref="Upserts"/> for the race handling).</summary>
    private static async Task<SessionEntity> EnsureSessionAsync(
        NachosDbContext db,
        long workspaceId,
        string name,
        string metadata,
        string configuration,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (await db.Sessions.AsNoTracking().Named(workspaceId, name).FirstOrDefaultAsync(ct) is { } existing)
        {
            return existing;
        }

        try
        {
            var inserted = await db.Sessions
                .FromSqlRaw(
                    "INSERT dbo.Sessions (WorkspaceId, Name, LifecycleState, Metadata, Configuration, CreatedAt) OUTPUT inserted.* VALUES (@workspace, @name, 0, @metadata, @configuration, @now)",
                    SqlParameters.Long("@workspace", workspaceId),
                    SqlParameters.Text("@name", name),
                    SqlParameters.LongText("@metadata", metadata),
                    SqlParameters.LongText("@configuration", configuration),
                    SqlParameters.Time("@now", now))
                .AsNoTracking()
                .ToListAsync(ct);
            return inserted.Single();
        }
        catch (Exception ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            return await db.Sessions.AsNoTracking().Named(workspaceId, name).FirstOrDefaultAsync(ct)
                ?? throw Lookups.TrailingSpaceCollision("session", name);
        }
    }

    private static IQueryable<SessionPeerEntity> ActiveMember(NachosDbContext db, SessionKey session, string peerName) =>
        db.SessionPeers.AsNoTracking().Where(member =>
            member.WorkspaceId == session.WorkspaceId
            && member.SessionId == session.SessionId
            && member.LeftAt == null
            && db.Peers.Any(peer => peer.Id == member.PeerId
                && peer.Name == peerName
                && EF.Functions.DataLength(peer.Name) == EF.Functions.DataLength(peerName)));

    private static NotFoundException NotAnActiveMember(string sessionName, string peerName) =>
        new($"Peer '{peerName}' is not an active member of session '{sessionName}'.");
}
