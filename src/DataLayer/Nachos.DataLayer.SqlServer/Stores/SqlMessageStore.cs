using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;
using Nachos.Abstractions.Domain;
using Nachos.Abstractions.Filtering;
using Nachos.Abstractions.Observation;
using Nachos.Abstractions.Stores;
using Nachos.Abstractions.Work;
using Nachos.DataLayer.SqlServer.Entities;
using Nachos.DataLayer.SqlServer.Filtering;
using Nachos.DataLayer.SqlServer.Storage;

namespace Nachos.DataLayer.SqlServer.Stores;

internal sealed class SqlMessageStore(SqlStoreRuntime runtime) : IMessageStore
{
    // 7 parameters per message plus 3 shared stay well under SQL Server's 2,100-parameter limit.
    private const int InsertChunk = 200;

    private const int WorkItemChunk = 400;

    // 3 parameters per chunk plus 3 shared.
    private const int ChunkInsertChunk = 600;

    public async Task<IReadOnlyList<MessageRecord>> ListRangeAsync(
        string workspaceName, string sessionName, long fromSeq, long toSeq, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        await using var db = await runtime.OpenAsync(ct);

        var session = await Lookups.RequireSessionAsync(db, workspaceName, sessionName, ct);
        var rows = await (
                from message in db.Messages.AsNoTracking()
                where message.SessionId == session.SessionId && message.Seq >= fromSeq && message.Seq <= toSeq
                join peer in db.Peers on message.PeerId equals peer.Id
                orderby message.Seq
                select new { Message = message, PeerName = peer.Name })
            .ToListAsync(ct);
        return [.. rows.Select(row => row.Message.ToRecord(workspaceName, sessionName, row.PeerName))];
    }

    /// <remarks>
    /// <para>
    /// One transaction, in the lock order documented on <see cref="Upserts"/>: (0) the workspace's S lock
    /// (<see cref="WriteAdmission"/>); (1) the <c>Seq</c> allocation
    /// <c>UPDATE Sessions SET NextMessageSeq += n OUTPUT deleted.NextMessageSeq</c>, whose exclusive row lock serializes
    /// appends to the session and makes the allocated range contiguous, and is the session's barrier lock: a session being
    /// deleted is refused under it; (2) the idempotency key: an expired record is
    /// deleted, and the key's range is then read under <c>UPDLOCK, HOLDLOCK</c>, so of concurrent appends with one key
    /// (in any session of the workspace) exactly one proceeds and the rest throw
    /// <see cref="IdempotencyDuplicateException"/>; (3) sender peers and memberships; (4) the messages; (5) their chunks,
    /// pending in <c>dbo.MessageEmbeddings</c>; (6) the work items (below), representation items before summary items;
    /// (7) <see cref="IdempotencyWrite.SerializeResponse"/>; (8) the idempotency record; commit. Any exception, the
    /// serializer's included, rolls everything back, so there is no <c>Seq</c> gap and no chunk or work without its message.
    /// </para>
    /// <para>
    /// <b>Enqueue</b> (spec §10.2): for each message whose <see cref="DeriveIntent.ShouldDerive"/> is set, the
    /// observers are resolved with <see cref="ObserverRule"/> at the message's <c>CreatedAt</c>, over the session's
    /// memberships as this transaction leaves them (the <c>Seq</c> allocation's session lock keeps them stable), and one
    /// <see cref="WorkTaskType.Representation"/> item is inserted when there is at least one. Its payload snapshots the
    /// observers and the message's own reasoning settings. A null <see cref="MessageAppend.Derive"/> enqueues nothing.
    /// For each summary kind whose <see cref="SummaryIntent.DueThrough"/> falls in the batch's <c>Seq</c> range, one
    /// <see cref="WorkTaskType.Summary"/> item, on the message at that <c>Seq</c>, with no tokens.
    /// </para>
    /// <para>
    /// The serializer gets its own deep copies of the staged records. While it runs, every entry point of this store
    /// rejects calls from its execution context (see <see cref="ReentryGuard"/>), and an attempted call fails the append
    /// even if the serializer swallowed the rejection.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<MessageRecord>> AppendAsync(
        string workspaceName, string sessionName, MessageAppend append, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        ArgumentNullException.ThrowIfNull(append);
        var (messages, idempotency, derive) = (append.Messages, append.Idempotency, append.Derive);
        RequireOnePerMessage(derive?.Count, messages.Count, nameof(append.Derive));
        RequireOnePerMessage(append.Chunks?.Count, messages.Count, nameof(append.Chunks));
        if (append.Chunks?.Any(chunks => chunks is null || chunks.Count > MessageChunk.MaxChunkIndex + 1 || chunks.Any(chunk => chunk is null)) == true)
        {
            throw new ArgumentException(
                $"A message may have at most {MessageChunk.MaxChunkIndex + 1} chunks, none of them null.", nameof(append));
        }

        ColumnLimits.RequireNames(messages.Select(message => message.PeerName));
        if (idempotency is not null)
        {
            ColumnLimits.RequireIdempotency(idempotency.Key, idempotency.RequestHash);
        }

        var storedMetadata = messages.Select(message => SqlJson.ToStorage(message.Metadata, JsonField.Metadata)).ToList();
        await using var db = await runtime.OpenAsync(ct);
        var now = runtime.Clock.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var workspaceId = await WriteAdmission.AdmitWorkspaceAsync(db, workspaceName, ct);
        var allocation = (await db.Database
            .SqlQueryRaw<SeqAllocation>(
                """
                UPDATE dbo.Sessions SET NextMessageSeq = NextMessageSeq + @count
                OUTPUT deleted.Id AS SessionId, deleted.NextMessageSeq AS FirstSeq, deleted.LifecycleState
                WHERE WorkspaceId = @workspace AND Name = @sessionName AND DATALENGTH(Name) = DATALENGTH(@sessionName)
                """,
                SqlParameters.Long("@count", messages.Count),
                SqlParameters.Long("@workspace", workspaceId),
                SqlParameters.Text("@sessionName", sessionName))
            .ToListAsync(ct))
            .SingleOrDefault() ?? throw Lookups.SessionNotFound(workspaceName, sessionName);
        if (allocation.LifecycleState == WriteAdmission.Deleting)
        {
            // Rolled back with the transaction, like the allocation.
            throw WriteAdmission.SessionDeleting();
        }

        var session = new SessionKey(workspaceId, allocation.SessionId);

        byte[]? keyHash = null;
        if (idempotency is not null)
        {
            keyHash = SHA256.HashData(Encoding.UTF8.GetBytes(idempotency.Key));
            await ClaimIdempotencyKeyAsync(db, session.WorkspaceId, idempotency.Key, keyHash, now, ct);
        }

        var senderPeers = new Dictionary<string, PeerEntity>(StringComparer.Ordinal);
        foreach (var name in messages.Select(message => message.PeerName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var peer = await Upserts.EnsurePeerAsync(db, session.WorkspaceId, name, "{}", "{}", isInternal: false, now, ct);
            senderPeers.Add(name, peer);
            await JoinAsSenderAsync(db, session, peer.Id, SenderJoin.JoinedAt(messages, name, now), ct);
        }

        var senders = senderPeers.ToDictionary(pair => pair.Key, pair => pair.Value.Id, StringComparer.Ordinal);
        var staged = await InsertMessagesAsync(db, session, allocation.FirstSeq, messages, storedMetadata, senders, now, ct);
        var records = staged.Select((entity, i) => entity.ToRecord(workspaceName, sessionName, messages[i].PeerName)).ToList();

        if (append.Chunks is not null)
        {
            await InsertChunksAsync(db, session, staged, append.Chunks, now, ct);
        }

        var work = derive is not null && derive.Any(intent => intent.ShouldDerive)
            ? await RepresentationWorkAsync(db, session, workspaceName, sessionName, staged, records, derive, senderPeers, ct)
            : [];
        if (append.Summary is { } summary)
        {
            work.AddRange(SummaryWork(workspaceName, sessionName, staged, summary));
        }

        await InsertWorkItemsAsync(db, session, work, now, ct);

        if (idempotency is not null)
        {
            var body = ReentryGuard.Invoke(idempotency.SerializeResponse, [.. records.Select(Records.Copy)]);
            await InsertIdempotencyRecordAsync(db, session.WorkspaceId, idempotency, keyHash!, body, now, ct);
        }

        await transaction.CommitAsync(ct);
        return records;
    }

    public async Task<MessageRecord?> GetAsync(
        string workspaceName, string sessionName, string publicId, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
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
        ReentryGuard.ThrowIfReentered();
        var storedMetadata = SqlJson.ToStorage(metadata, JsonField.Metadata);
        await using var db = await runtime.OpenAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var session = await WriteAdmission.AdmitSessionAsync(db, workspaceName, sessionName, ct);
        var updated = await JsonUpdate.ApplyAsync(
            db.Messages,
            "dbo.Messages",
            "SessionId = @session AND PublicId = @publicId AND DATALENGTH(PublicId) = DATALENGTH(@publicId)",
            storedMetadata,
            configuration: null,
            [SqlParameters.Long("@session", session.SessionId), SqlParameters.Text("@publicId", publicId)],
            ct) ?? throw new NotFoundException($"Message '{publicId}' not found in session '{sessionName}'.");
        var peerName = await db.Peers.Where(peer => peer.Id == updated.PeerId).Select(peer => peer.Name).SingleAsync(ct);
        await transaction.CommitAsync(ct);
        return updated.ToRecord(workspaceName, sessionName, peerName);
    }

    public async Task<Page<MessageRecord>> ListAsync(
        string workspaceName, string sessionName, FilterNode? filter, PageRequest page, CancellationToken ct)
    {
        ReentryGuard.ThrowIfReentered();
        var (where, parameters) = SqlFilterCompiler.Compile(filter, ResourceKind.Message, "t");
        await using var db = await runtime.OpenAsync(ct);

        var session = await Lookups.RequireSessionAsync(db, workspaceName, sessionName, ct);
        var rows = db.Messages
            .FromSqlRaw(
                string.Concat("SELECT * FROM dbo.Messages AS t WHERE t.SessionId = @session AND ", where),
                [SqlParameters.Long("@session", session.SessionId), .. parameters])
            .AsNoTracking();
        return await Paging.ToFilteredPageAsync<MessageEntity, MessageRecord>(
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

    /// <exception cref="ArgumentException">A list is given and does not have one entry per message.</exception>
    private static void RequireOnePerMessage(int? count, int messageCount, string name)
    {
        if (count is { } given && given != messageCount)
        {
            throw new ArgumentException($"Expected one {name} entry per message ({messageCount}), got {given}.", name);
        }
    }

    /// <summary>Inserts each message's chunks as pending embeddings, in message then chunk order.</summary>
    private static async Task InsertChunksAsync(
        NachosDbContext db,
        SessionKey session,
        List<MessageEntity> staged,
        IReadOnlyList<IReadOnlyList<string>> chunks,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var all = staged
            .Zip(chunks)
            .SelectMany(pair => pair.Second.Select((content, index) => (MessageId: pair.First.Id, Index: index, Content: content)))
            .ToList();
        for (var start = 0; start < all.Count; start += ChunkInsertChunk)
        {
            var count = Math.Min(ChunkInsertChunk, all.Count - start);
            var rows = new List<string>(count);
            var parameters = new List<SqlParameter>
            {
                SqlParameters.Long("@workspace", session.WorkspaceId),
                SqlParameters.Long("@session", session.SessionId),
                SqlParameters.Time("@now", now),
            };
            for (var i = start; i < start + count; i++)
            {
                rows.Add($"(@workspace, @message{i}, @session, @index{i}, @content{i}, 0, @now, {i})");
                parameters.Add(SqlParameters.Long($"@message{i}", all[i].MessageId));
                parameters.Add(SqlParameters.Int($"@index{i}", all[i].Index));
                parameters.Add(SqlParameters.LongText($"@content{i}", all[i].Content));
            }

            await db.Database.ExecuteSqlRawAsync(
                string.Concat(
                    "INSERT dbo.MessageEmbeddings (WorkspaceId, MessageId, SessionId, ChunkIndex, Content, SyncState, CreatedAt) " +
                    "SELECT WorkspaceId, MessageId, SessionId, ChunkIndex, Content, SyncState, CreatedAt FROM (VALUES ",
                    string.Join(", ", rows),
                    ") AS v (WorkspaceId, MessageId, SessionId, ChunkIndex, Content, SyncState, CreatedAt, Ordinal) ORDER BY Ordinal"),
                parameters,
                ct);
        }
    }

    /// <summary>One summary item per kind whose threshold the staged <c>Seq</c> range crosses, on the message at that <c>Seq</c>.</summary>
    private static IEnumerable<PendingWork> SummaryWork(
        string workspaceName, string sessionName, List<MessageEntity> staged, SummaryIntent summary)
    {
        if (staged.Count == 0)
        {
            yield break;
        }

        var first = staged[0].Seq;
        foreach (var kind in Enum.GetValues<SummaryKind>())
        {
            if (summary.DueThrough(kind, first, staged[^1].Seq) is { } throughSeq)
            {
                yield return new PendingWork(
                    WorkTaskType.Summary,
                    WorkUnitKeys.Summary(workspaceName, sessionName),
                    SummaryPayload.Create(kind, throughSeq).ToJsonString(),
                    TokenCount: 0,
                    staged[(int)(throughSeq - first)].Id);
            }
        }
    }

    /// <summary>
    /// One representation item per derived message whose sender has an observer: its unit key, payload, token count and
    /// message row. Reads the session's memberships inside the append's transaction, so they include its own joins.
    /// </summary>
    private static async Task<List<PendingWork>> RepresentationWorkAsync(
        NachosDbContext db,
        SessionKey session,
        string workspaceName,
        string sessionName,
        List<MessageEntity> staged,
        List<MessageRecord> records,
        IReadOnlyList<DeriveIntent> derive,
        Dictionary<string, PeerEntity> senderPeers,
        CancellationToken ct)
    {
        var memberships = await (
                from member in db.SessionPeers.AsNoTracking()
                where member.WorkspaceId == session.WorkspaceId && member.SessionId == session.SessionId
                join peer in db.Peers on member.PeerId equals peer.Id
                select new { peer.Name, member.Configuration, member.JoinedAt, member.LeftAt })
            .ToListAsync(ct);
        var configs = memberships.ToDictionary(m => m.Name, m => Records.DeserializePeerConfig(m.Configuration), StringComparer.Ordinal);
        var members = memberships
            .Select(m => new MemberObservation(m.Name, configs[m.Name].ObserveOthers, m.JoinedAt, m.LeftAt))
            .ToList();

        var work = new List<PendingWork>();
        for (var i = 0; i < records.Count; i++)
        {
            var (message, intent) = (records[i], derive[i]);
            if (!intent.ShouldDerive)
            {
                continue;
            }

            var sender = PeerObservation.From(
                message.PeerName, SqlJson.FromStorage(senderPeers[message.PeerName].Configuration), configs[message.PeerName]);
            var observers = ObserverRule.Resolve(sender, members, message.CreatedAt);
            if (observers.Count > 0)
            {
                work.Add(new PendingWork(
                    WorkTaskType.Representation,
                    WorkUnitKeys.Representation(workspaceName, sessionName, message.PeerName),
                    RepresentationPayload.Create(observers, intent.ReasoningEnabled, intent.CustomInstructions).ToJsonString(),
                    message.TokenCount,
                    staged[i].Id));
            }
        }

        return work;
    }

    /// <summary>
    /// Inserts pending work items, available now. <c>INSERT ... SELECT ... ORDER BY</c> assigns identity values in that
    /// order, which a plain multi-row <c>VALUES</c> does not guarantee, so the ids follow message order.
    /// </summary>
    private static async Task InsertWorkItemsAsync(
        NachosDbContext db, SessionKey session, List<PendingWork> work, DateTimeOffset now, CancellationToken ct)
    {
        for (var start = 0; start < work.Count; start += WorkItemChunk)
        {
            var count = Math.Min(WorkItemChunk, work.Count - start);
            var rows = new List<string>(count);
            var parameters = new List<SqlParameter>
            {
                SqlParameters.Long("@workspace", session.WorkspaceId),
                SqlParameters.Long("@session", session.SessionId),
                SqlParameters.Time("@now", now),
            };
            for (var i = start; i < start + count; i++)
            {
                rows.Add($"(@workspace, @type{i}, @key{i}, @payload{i}, 0, 0, @now, @tokens{i}, @message{i}, @session, @now, {i})");
                parameters.Add(SqlParameters.Byte($"@type{i}", (byte)work[i].TaskType));
                parameters.Add(SqlParameters.Text($"@key{i}", work[i].WorkUnitKey));
                parameters.Add(SqlParameters.LongText($"@payload{i}", work[i].Payload));
                parameters.Add(SqlParameters.Int($"@tokens{i}", work[i].TokenCount));
                parameters.Add(SqlParameters.Long($"@message{i}", work[i].MessageId));
            }

            await db.Database.ExecuteSqlRawAsync(
                string.Concat(
                    "INSERT dbo.WorkItems (WorkspaceId, TaskType, WorkUnitKey, Payload, Status, Attempts, AvailableAt, TokenCount, MessageId, SessionId, CreatedAt) " +
                    "SELECT WorkspaceId, TaskType, WorkUnitKey, Payload, Status, Attempts, AvailableAt, TokenCount, MessageId, SessionId, CreatedAt " +
                    "FROM (VALUES ",
                    string.Join(", ", rows),
                    ") AS v (WorkspaceId, TaskType, WorkUnitKey, Payload, Status, Attempts, AvailableAt, TokenCount, MessageId, SessionId, CreatedAt, Ordinal) ORDER BY Ordinal"),
                parameters,
                ct);
        }
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
    /// Makes the sender an active member from <paramref name="joinedAt"/>: a new member joins with the default config, a
    /// former member is reactivated in place keeping its config, an active member is untouched. The append holds the
    /// session row lock.
    /// </summary>
    private static async Task JoinAsSenderAsync(
        NachosDbContext db, SessionKey session, long peerId, DateTimeOffset joinedAt, CancellationToken ct) =>
        await db.Database.ExecuteSqlRawAsync(
            """
            UPDATE dbo.SessionPeers SET JoinedAt = @joinedAt, LeftAt = NULL
            WHERE WorkspaceId = @workspace AND SessionId = @session AND PeerId = @peer AND LeftAt IS NOT NULL;
            IF NOT EXISTS (SELECT 1 FROM dbo.SessionPeers WHERE WorkspaceId = @workspace AND SessionId = @session AND PeerId = @peer)
                INSERT dbo.SessionPeers (WorkspaceId, SessionId, PeerId, Configuration, JoinedAt)
                VALUES (@workspace, @session, @peer, @config, @joinedAt);
            """,
            [
                SqlParameters.Time("@joinedAt", joinedAt),
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

    /// <summary>The row the <c>Seq</c> allocation outputs: the session's key, the first allocated value and its state.</summary>
    private sealed record SeqAllocation(long SessionId, long FirstSeq, byte LifecycleState);

    /// <summary>A work item to insert for the message row <paramref name="MessageId"/>.</summary>
    private sealed record PendingWork(WorkTaskType TaskType, string WorkUnitKey, string Payload, int TokenCount, long MessageId);
}
