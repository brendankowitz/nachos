using Microsoft.EntityFrameworkCore;
using Nachos.Abstractions.Contracts;
using Nachos.DataLayer.SqlServer.Entities;

namespace Nachos.DataLayer.SqlServer.Storage;

/// <summary>
/// The get-or-create steps shared by the stores, written to be race-safe inside or outside a transaction.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lock order.</b> Every transaction that writes memberships first takes the session row (an update lock via
/// <see cref="LockSessionAsync"/>, or the exclusive lock of the append's <c>Seq</c> allocation), then the idempotency
/// key range (appends only), then peers in ordinal name order, then memberships. Membership writers are therefore
/// serialized per session, which rules out lock cycles between them (row scans in id order against appends in name
/// order would otherwise deadlock), and makes the read-then-insert of a membership race-free.
/// </para>
/// <para>
/// <b>Get-or-create</b> reads first (the common case is "exists"); on a miss it inserts, and a unique violation
/// (2627/2601) means a concurrent caller won, so it re-reads the committed winner. Under read committed snapshot that
/// re-read sees the winner: the losing insert waited on the winner's key lock until it committed.
/// </para>
/// </remarks>
internal static class Upserts
{
    /// <summary>Returns the peer, inserting it when missing.</summary>
    public static async Task<PeerEntity> EnsurePeerAsync(
        NachosDbContext db,
        long workspaceId,
        string name,
        string metadata,
        string configuration,
        bool isInternal,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (await db.Peers.AsNoTracking().Named(workspaceId, name).FirstOrDefaultAsync(ct) is { } existing)
        {
            return existing;
        }

        try
        {
            var inserted = await db.Peers
                .FromSqlRaw(
                    "INSERT dbo.Peers (WorkspaceId, Name, IsInternal, Metadata, Configuration, CreatedAt) OUTPUT inserted.* VALUES (@workspace, @name, @internal, @metadata, @configuration, @now)",
                    SqlParameters.Long("@workspace", workspaceId),
                    SqlParameters.Text("@name", name),
                    SqlParameters.Bit("@internal", isInternal),
                    SqlParameters.LongText("@metadata", metadata),
                    SqlParameters.LongText("@configuration", configuration),
                    SqlParameters.Time("@now", now))
                .AsNoTracking()
                .ToListAsync(ct);
            return inserted.Single();
        }
        catch (Exception ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            return await db.Peers.AsNoTracking().Named(workspaceId, name).FirstOrDefaultAsync(ct)
                ?? throw Lookups.TrailingSpaceCollision("peer", name);
        }
    }

    /// <summary>Holds the session row's update lock until the transaction ends; the first step of the lock order.</summary>
    public static Task LockSessionAsync(NachosDbContext db, long sessionId, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            "SELECT Id FROM dbo.Sessions WITH (UPDLOCK, ROWLOCK) WHERE Id = @session",
            [SqlParameters.Long("@session", sessionId)],
            ct);

    /// <summary>
    /// Makes each peer an active member with its config, creating missing peers. An active member keeps its
    /// <c>JoinedAt</c>; anyone else (new or former member) joins now, reusing a former member's row. The caller holds
    /// the session lock.
    /// </summary>
    public static async Task ActivateMembersAsync(
        NachosDbContext db,
        SessionKey session,
        IReadOnlyDictionary<string, SessionPeerConfig> peers,
        DateTimeOffset now,
        CancellationToken ct)
    {
        foreach (var (name, config) in peers.OrderBy(peer => peer.Key, StringComparer.Ordinal))
        {
            var peer = await EnsurePeerAsync(db, session.WorkspaceId, name, "{}", "{}", isInternal: false, now, ct);
            await db.Database.ExecuteSqlRawAsync(
                """
                UPDATE dbo.SessionPeers
                SET Configuration = @config,
                    JoinedAt = CASE WHEN LeftAt IS NULL THEN JoinedAt ELSE @now END,
                    LeftAt = NULL
                WHERE WorkspaceId = @workspace AND SessionId = @session AND PeerId = @peer;
                IF @@ROWCOUNT = 0
                    INSERT dbo.SessionPeers (WorkspaceId, SessionId, PeerId, Configuration, JoinedAt)
                    VALUES (@workspace, @session, @peer, @config, @now);
                """,
                [
                    SqlParameters.Long("@workspace", session.WorkspaceId),
                    SqlParameters.Long("@session", session.SessionId),
                    SqlParameters.Long("@peer", peer.Id),
                    SqlParameters.LongText("@config", Records.Serialize(config)),
                    SqlParameters.Time("@now", now),
                ],
                ct);
        }
    }
}
