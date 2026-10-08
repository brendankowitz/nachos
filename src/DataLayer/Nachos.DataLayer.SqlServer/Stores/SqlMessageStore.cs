using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
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

internal sealed class SqlMessageStore(SqlStoreRuntime runtime) : IMessageStore
{
    // 7 parameters per message plus 3 shared stay well under SQL Server's 2,100-parameter limit.
    private const int InsertChunk = 200;

    /// <remarks>
    /// <para>
    /// One transaction, in the lock order documented on <see cref="Upserts"/>: (1) the <c>Seq</c> allocation
    /// <c>UPDATE Sessions SET NextMessageSeq += n OUTPUT deleted.NextMessageSeq</c>, whose exclusive row lock serializes
    /// appends to the session and makes the allocated range contiguous; (2) the idempotency key: an expired record is
    /// deleted, and the key's range is then read under <c>UPDLOCK, HOLDLOCK</c>, so of concurrent appends with one key
    /// (in any session of the workspace) exactly one proceeds and the rest throw
    /// <see cref="IdempotencyDuplicateException"/>; (3) sender peers and memberships; (4) the messages; (5)
    /// <see cref="IdempotencyWrite.SerializeResponse"/>; (6) the idempotency record; commit. Any exception, the
    /// serializer's included, rolls everything back, so there is no <c>Seq</c> gap.
    /// </para>
    /// <para>
    /// The serializer gets its own deep copies of the staged records. While it runs, every entry point of this store
    /// rejects calls from its execution context (see <see cref="ReentryGuard"/>), and an attempted call fails the append
    /// even if the serializer swallowed the rejection.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<MessageRecord>> AppendAsync(
        string workspaceName,
        string sessionName,
        IReadOnlyList<NewMessage> messages,
        IdempotencyWrite? idempotency,
        CancellationToken ct)
    {
        runtime.Guard.ThrowIfReentered();
        var storedMetadata = messages.Select(message => SqlJson.ToStorage(message.Metadata, "metadata")).ToList();
        await using var db = await runtime.OpenAsync(ct);
        var now = runtime.Clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var allocation = (await db.Database
            .SqlQueryRaw<SeqAllocation>(
                """
                UPDATE s SET NextMessageSeq = s.NextMessageSeq + @count
                OUTPUT deleted.WorkspaceId, deleted.Id AS SessionId, deleted.NextMessageSeq AS FirstSeq
                FROM dbo.Sessions AS s
                JOIN dbo.Workspaces AS w ON w.Id = s.WorkspaceId
                WHERE w.Name = @workspaceName AND DATALENGTH(w.Name) = DATALENGTH(@workspaceName)
                  AND s.Name = @sessionName AND DATALENGTH(s.Name) = DATALENGTH(@sessionName)
                """,
                SqlParameters.Long("@count", messages.Count),
                SqlParameters.Text("@workspaceName", workspaceName),
                SqlParameters.Text("@sessionName", sessionName))
            .ToListAsync(ct))
            .SingleOrDefault() ?? throw Lookups.SessionNotFound(workspaceName, sessionName);
        var session = new SessionKey(allocation.WorkspaceId, allocation.SessionId);

        byte[]? keyHash = null;
        if (idempotency is not null)
        {
            keyHash = SHA256.HashData(Encoding.UTF8.GetBytes(idempotency.Key));
            await ClaimIdempotencyKeyAsync(db, session.WorkspaceId, idempotency.Key, keyHash, now, ct);
        }

        var senders = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var name in messages.Select(message => message.PeerName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var peer = await Upserts.EnsurePeerAsync(db, session.WorkspaceId, name, "{}", "{}", isInternal: false, now, ct);
            senders.Add(name, peer.Id);
            await JoinAsSenderAsync(db, session, peer.Id, now, ct);
        }

        var staged = await InsertMessagesAsync(db, session, allocation.FirstSeq, messages, storedMetadata, senders, now, ct);
        var records = staged.Select((entity, i) => entity.ToRecord(workspaceName, sessionName, messages[i].PeerName)).ToList();

        if (idempotency is not null)
        {
            var body = runtime.Guard.Invoke(idempotency.SerializeResponse, [.. records.Select(Records.Copy)]);
            await InsertIdempotencyRecordAsync(db, session.WorkspaceId, idempotency, keyHash!, body, now, ct);
        }

        await transaction.CommitAsync(ct);
        return records;
    }

    public async Task<MessageRecord?> GetAsync(
        string workspaceName, string sessionName, string publicId, CancellationToken ct)
    {
        runtime.Guard.ThrowIfReentered();
        await using var db = await runtime.OpenAsync(ct);

        var session = await Lookups.RequireSessionAsync(db, workspaceName, sessionName, ct);
        var row = await (
                from message in db.Messages.AsNoTracking().Identified(session.SessionId, publicId)
                join peer in db.Peers on message.PeerId equals peer.Id
                select new { Message = message, PeerName = peer.Name })
            .FirstOrDefaultAsync(ct);
        return row?.Message.ToRecord(workspaceName, sessionName, row.PeerName);
    }

    public async Task<MessageRecord> UpdateMetadataAsync(
        string workspaceName, string sessionName, string publicId, JsonObject metadata, CancellationToken ct)
    {
        runtime.Guard.ThrowIfReentered();
        var storedMetadata = SqlJson.ToStorage(metadata, "metadata");
        await using var db = await runtime.OpenAsync(ct);

        var session = await Lookups.RequireSessionAsync(db, workspaceName, sessionName, ct);
        var updated = await JsonUpdate.ApplyAsync(
            db.Messages,
            "dbo.Messages",
            "SessionId = @session AND PublicId = @publicId AND DATALENGTH(PublicId) = DATALENGTH(@publicId)",
            storedMetadata,
            configuration: null,
            [SqlParameters.Long("@session", session.SessionId), SqlParameters.Text("@publicId", publicId)],
            ct) ?? throw new NotFoundException($"Message '{publicId}' not found in session '{sessionName}'.");
        var peerName = await db.Peers.Where(peer => peer.Id == updated.PeerId).Select(peer => peer.Name).SingleAsync(ct);
        return updated.ToRecord(workspaceName, sessionName, peerName);
    }

    public async Task<Page<MessageRecord>> ListAsync(
        string workspaceName, string sessionName, FilterNode? filter, PageRequest page, CancellationToken ct)
    {
        runtime.Guard.ThrowIfReentered();
        var (where, parameters) = SqlFilterCompiler.Compile(filter, ResourceKind.Message, "t");
        await using var db = await runtime.OpenAsync(ct);

        var session = await Lookups.RequireSessionAsync(db, workspaceName, sessionName, ct);
        var rows = db.Messages
            .FromSqlRaw(
                string.Concat("SELECT * FROM dbo.Messages AS t WHERE t.SessionId = @session AND ", where),
                [SqlParameters.Long("@session", session.SessionId), .. parameters])
            .AsNoTracking();
        return await Paging.ToPageAsync<MessageEntity, MessageRecord>(
            rows,
            page,
            (query, reverse) => reverse ? query.OrderByDescending(m => m.Seq) : query.OrderBy(m => m.Seq),
            async entities =>
            {
                var peerIds = entities.Select(m => m.PeerId).Distinct().ToList();
                var names = await db.Peers.Where(peer => peerIds.Contains(peer.Id)).ToDictionaryAsync(peer => peer.Id, peer => peer.Name, ct);
                return [.. entities.Select(m => m.ToRecord(workspaceName, sessionName, names[m.PeerId]))];
            },
            ct);
    }

    /// <summary>
    /// Deletes an expired record for the key, then reads the key's range under <c>UPDLOCK, HOLDLOCK</c> (held to commit):
    /// a live record means a duplicate; otherwise no concurrent append can claim the key before this one commits.
    /// </summary>
    private static async Task ClaimIdempotencyKeyAsync(
        NachosDbContext db, long workspaceId, string key, byte[] keyHash, DateTimeOffset now, CancellationToken ct)
    {
        var live = await db.Database
            .SqlQueryRaw<int>(
                """
                DELETE dbo.IdempotencyRecords WHERE WorkspaceId = @workspace AND KeyHash = @keyHash AND ExpiresAt <= @now;
                SELECT COUNT(*) AS [Value] FROM dbo.IdempotencyRecords WITH (UPDLOCK, HOLDLOCK)
                WHERE WorkspaceId = @workspace AND KeyHash = @keyHash;
                """,
                SqlParameters.Long("@workspace", workspaceId),
                SqlParameters.Binary("@keyHash", keyHash),
                SqlParameters.Time("@now", now))
            .ToListAsync(ct);
        if (live.Single() > 0)
        {
            throw new IdempotencyDuplicateException(key);
        }
    }

    /// <summary>
    /// Makes the sender an active member: a new member joins with the default config, a former member is reactivated in
    /// place keeping its config, an active member is untouched. The append holds the session row lock.
    /// </summary>
    private static async Task JoinAsSenderAsync(NachosDbContext db, SessionKey session, long peerId, DateTimeOffset now, CancellationToken ct) =>
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE dbo.SessionPeers SET JoinedAt = @now, LeftAt = NULL
            WHERE WorkspaceId = @workspace AND SessionId = @session AND PeerId = @peer AND LeftAt IS NOT NULL;
            IF NOT EXISTS (SELECT 1 FROM dbo.SessionPeers WHERE WorkspaceId = @workspace AND SessionId = @session AND PeerId = @peer)
                INSERT dbo.SessionPeers (WorkspaceId, SessionId, PeerId, Configuration, JoinedAt)
                VALUES (@workspace, @session, @peer, @config, @now);
            """,
            [
                SqlParameters.Time("@now", now),
                SqlParameters.Long("@workspace", session.WorkspaceId),
                SqlParameters.Long("@session", session.SessionId),
                SqlParameters.Long("@peer", peerId),
                SqlParameters.LongText("@config", Records.DefaultPeerConfig),
            ],
            ct);

    /// <summary>Inserts the messages with <c>Seq</c> from <paramref name="firstSeq"/> and returns the stored rows in message order.</summary>
    private static async Task<List<MessageEntity>> InsertMessagesAsync(
        NachosDbContext db,
        SessionKey session,
        long firstSeq,
        IReadOnlyList<NewMessage> messages,
        List<string> storedMetadata,
        Dictionary<string, long> senders,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var publicIds = new HashSet<string>(StringComparer.Ordinal);
        var stored = new Dictionary<string, MessageEntity>(StringComparer.Ordinal);
        var orderedIds = new List<string>(messages.Count);
        for (var start = 0; start < messages.Count; start += InsertChunk)
        {
            var count = Math.Min(InsertChunk, messages.Count - start);
            var rows = new List<string>(count);
            var parameters = new List<SqlParameter>
            {
                SqlParameters.Long("@workspace", session.WorkspaceId),
                SqlParameters.Long("@session", session.SessionId),
            };
            for (var i = start; i < start + count; i++)
            {
                string publicId;
                do
                {
                    publicId = PublicId.New();
                }
                while (!publicIds.Add(publicId));

                orderedIds.Add(publicId);
                var message = messages[i];
                rows.Add($"(@workspace, @session, @p{i}, @id{i}, @seq{i}, @content{i}, @tokens{i}, @metadata{i}, @created{i})");
                parameters.Add(SqlParameters.Long($"@p{i}", senders[message.PeerName]));
                parameters.Add(SqlParameters.Text($"@id{i}", publicId));
                parameters.Add(SqlParameters.Long($"@seq{i}", firstSeq + i));
                parameters.Add(SqlParameters.LongText($"@content{i}", message.Content));
                parameters.Add(SqlParameters.Int($"@tokens{i}", message.TokenCount));
                parameters.Add(SqlParameters.LongText($"@metadata{i}", storedMetadata[i]));
                parameters.Add(SqlParameters.Time($"@created{i}", message.CreatedAt ?? now));
            }

            var sql = string.Concat(
                "INSERT dbo.Messages (WorkspaceId, SessionId, PeerId, PublicId, Seq, Content, TokenCount, Metadata, CreatedAt) OUTPUT inserted.* VALUES ",
                string.Join(", ", rows));
            foreach (var entity in await db.Messages.FromSqlRaw(sql, [.. parameters]).AsNoTracking().ToListAsync(ct))
            {
                stored.Add(entity.PublicId, entity);
            }
        }

        return [.. orderedIds.Select(id => stored[id])];
    }

    private static async Task InsertIdempotencyRecordAsync(
        NachosDbContext db,
        long workspaceId,
        IdempotencyWrite idempotency,
        byte[] keyHash,
        string body,
        DateTimeOffset now,
        CancellationToken ct)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT dbo.IdempotencyRecords (WorkspaceId, KeyHash, [Key], RequestHash, ResponseStatus, ResponseBody, ExpiresAt)
                VALUES (@workspace, @keyHash, @key, @requestHash, @status, @body, @expiresAt)
                """,
                [
                    SqlParameters.Long("@workspace", workspaceId),
                    SqlParameters.Binary("@keyHash", keyHash),
                    SqlParameters.Text("@key", idempotency.Key),
                    SqlParameters.Ascii("@requestHash", idempotency.RequestHash),
                    SqlParameters.Int("@status", idempotency.ResponseStatus),
                    SqlParameters.LongText("@body", body),
                    SqlParameters.Time("@expiresAt", now + idempotency.Ttl),
                ],
                ct);
        }
        catch (Exception ex) when (SqlErrors.IsUniqueViolation(ex))
        {
            // Unreachable while the claim's range lock holds; kept so a lost race can only ever surface as a duplicate.
            throw new IdempotencyDuplicateException(idempotency.Key, ex);
        }
    }

    /// <summary>The row the <c>Seq</c> allocation outputs: the session's keys and the first allocated value.</summary>
    private sealed record SeqAllocation(long WorkspaceId, long SessionId, long FirstSeq);
}
